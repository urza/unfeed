using System.Globalization;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Feed.Core.Queries;

public sealed record Card(Post Post, Author? Author, Media[] Media, Like? Like, int Up, int Down, string? Why, string[] Trail);
public sealed record FeedUnit(Card Lead, Card[] Folded, string? Badge);
public sealed record FeedPage(InstanceSnapshot Instance, string View, string? Platform, string Scope, FeedUnit[] Items, int VisibleCount, int HiddenCount, int UnsortedCount, int Friends, string[] KnownPlatforms, PlatformState[] States, Run[] Runs, RunRequest[] Requests, string? LastVisit, string EmptyMessage, int AdsDropped, CoverageRow[] Coverage, string? ModelRetry, int TotalHidden, int TotalUnsorted, RecoveryIssue[]? Issues = null, DiagnosticLevel Diagnostics = DiagnosticLevel.None)
{
    // The hide owner the hidden scope is narrowed to (`?owner=`), so the banner and the Hidden link can show and keep it.
    public string? Owner { get; init; }
}
// What the Manage dot says: Notice (yellow) is lag the scheduler clears on its own, such as a timeline not
// swept recently or posts waiting for the model; Alert (red) is a failure or a login that needs the owner.
public enum DiagnosticLevel { None, Notice, Alert }
public sealed class FeedQuery(DbFactory factory)
{
    // owner narrows the hidden scope to one hide owner (`?hidden=1&owner=llm`); the counts stay those of the whole scope.
    public Task<FeedPage> Read(InstanceSnapshot instance, string view, string? platform, string scope, CancellationToken ct, long? authorId = null, string? category = null, string? owner = null)
        => Read(instance, view, platform, scope, ct, authorId, category, owner, null);

    public async Task<FeedPage> Read(InstanceSnapshot instance, string view, string? platform, string scope, CancellationToken ct, long? authorId, string? category, string? owner, Author[]? preloadedAuthors)
    {
        await using var db = factory.Open(); await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        await using var readTransaction = conn.BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(readTransaction, ct);

        // One covering-index pass over IX_Posts_FeedMembership yields known platforms and every platform-level count
        // (total, hidden, ads dropped, unsorted, global totals, and visible count for the "all" view).
        var platformStats = new Dictionary<string, (int Total, int Hidden, int Ads, int Unsorted)>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = readTransaction;
            cmd.CommandText = """
                SELECT Platform,
                       COUNT(*),
                       SUM(CASE WHEN Hidden != 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Hidden != 0 AND IsSponsored != 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Hidden = 0 AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision) THEN 1 ELSE 0 END)
                FROM Posts
                GROUP BY Platform
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                platformStats[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2), r.GetInt32(3), r.GetInt32(4));
        }

        var known = new List<string>(Platforms.All.Length);
        foreach (var p in Platforms.All) if (platformStats.ContainsKey(p)) known.Add(p);
        if (authorId is null && platform is not null && !known.Contains(platform)) throw new ArgumentException("unknown platform");
        var viewMap = instance.Taxonomy.Views.ToDictionary(v => v.Key, StringComparer.Ordinal);
        if (view != "all" && !viewMap.ContainsKey(view)) throw new ArgumentException("unknown view");
        var authors = preloadedAuthors ?? await SqliteDates.ReadAuthorsAsync(conn, readTransaction, ct);
        if (authorId is not null && !authors.Any(a => a.Id == authorId)) throw new KeyNotFoundException("Person not found");
        if (category is not null && !instance.Taxonomy.Categories.Any(c => c.Key == category)) throw new ArgumentException("unknown category");

        var enabled = Platforms.All.Where(instance.Config.Enabled).ToArray();
        var scopedPlatforms = platform is null ? enabled : [platform];
        var authorMap = new Dictionary<long, Author>(authors.Length);
        foreach (var a in authors) authorMap[a.Id] = a;
        var sharedRules = new ResolvedRules(authors, instance);
        var version = Prompts.Version(instance);
        var configHash = Prompts.ConfigurationHash(instance);

        // Author views are only needed for author-list/union views and for stacking protection on main feed pages.
        var stack = instance.Config.Ui.Stack;
        var authorViews = new Dictionary<string, HashSet<long>>(StringComparer.Ordinal);
        if (authorId is null && (stack.MinPosts > 0 || view != "all"))
        {
            foreach (var v in instance.Taxonomy.Views)
                if (v.Authors is not null)
                    authorViews[v.Key] = v.Authors.Value.SelectMany(e => sharedRules.Resolve(e)).Select(a => a.Id).ToHashSet();
        }

        int totalHidden = 0, totalUnsorted = 0;
        foreach (var stat in platformStats.Values) { totalHidden += stat.Hidden; totalUnsorted += stat.Unsorted; }

        int visible = 0, hidden = 0, unsorted = 0, adsDropped = 0;
        long[] ids;
        Dictionary<string, Dictionary<long, RareMatch>>? rareWhyByView = null;
        int renderCap = instance.Config.Ui.RenderCap;
        bool llmEnabled = instance.Config.Llm.Enabled;

        if (authorId is { } targetAuthorId)
        {
            // A person page scopes to one author: one narrow lookup via IX_Posts_AuthorHistory computes counts and top IDs.
            var top = new TopSelector(renderCap);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = readTransaction;
            cmd.CommandText = "SELECT Id, Hidden, IsSponsored, PostedAt, ContentRevision, VerdictContentRevision, CategoriesJson, HiddenBy FROM Posts WHERE AuthorId = $aid";
            cmd.Parameters.AddWithValue("$aid", targetAuthorId);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long id = r.GetInt64(0);
                bool isHidden = r.GetInt32(1) != 0;
                bool isSponsored = r.GetInt32(2) != 0;
                long? postedTicks = r.IsDBNull(3) ? null : SqliteDates.ParseTicks(r.GetString(3));
                int contentRev = r.GetInt32(4);
                int? verdictRev = r.IsDBNull(5) ? null : r.GetInt32(5);
                string? cats = r.IsDBNull(6) ? null : r.GetString(6);
                bool judged = cats is not null && verdictRev == contentRev;

                if (isHidden)
                {
                    hidden++;
                    if (isSponsored) adsDropped++;
                    if (scope == "hidden" && (owner is null || (!r.IsDBNull(7) && r.GetString(7) == owner)))
                        top.Offer(postedTicks, id);
                }
                else
                {
                    if (!judged) unsorted++;
                    bool match = category is null || judged && ContainsCategory(cats!, category);
                    if (match) visible++;
                    if (scope == "live" && match || scope == "unsorted" && !judged)
                        top.Offer(postedTicks, id);
                }
            }
            ids = top.ExtractIds();
        }
        else
        {
            int scopedTotal = 0;
            foreach (var p in scopedPlatforms)
            {
                if (!platformStats.TryGetValue(p, out var st)) continue;
                scopedTotal += st.Total; hidden += st.Hidden; adsDropped += st.Ads; unsorted += st.Unsorted;
            }

            if (view == "all")
            {
                visible = (scopedTotal - hidden) - (llmEnabled ? unsorted : 0);
                ids = scopedPlatforms.Length == 0 ? [] : await SelectIdsSimple(conn, readTransaction, scopedPlatforms, scope, llmEnabled, owner, renderCap, ct);
            }
            else if (scopedPlatforms.Length == 0)
            {
                visible = 0; ids = [];
            }
            else
            {
                var def = viewMap[view];
                if (def.Union is null && def.Authors is not null)
                {
                    // Narrow lookup via IX_Posts_AuthorHistory touches only this view's authors instead of scanning
                    // every live post in IX_Posts_FeedMembership.
                    var scopedSet = new HashSet<string>(scopedPlatforms, StringComparer.Ordinal);
                    var scopedAuthorIds = new List<long>();
                    foreach (var aid in authorViews[def.Key])
                        if (authorMap.TryGetValue(aid, out var a) && scopedSet.Contains(a.Platform)) scopedAuthorIds.Add(aid);
                    var top = scope == "live" ? new TopSelector(renderCap) : null;
                    if (scopedAuthorIds.Count > 0)
                    {
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = readTransaction;
                        cmd.CommandText = "SELECT Id, Platform, Hidden, PostedAt, ContentRevision, VerdictContentRevision, CategoriesJson FROM Posts WHERE AuthorId IN (SELECT value FROM json_each($aids))";
                        cmd.Parameters.AddWithValue("$aids", JsonSerializer.Serialize(scopedAuthorIds));
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        while (await r.ReadAsync(ct))
                        {
                            if (r.GetInt32(2) != 0 || !scopedSet.Contains(r.GetString(1))) continue;
                            if (def.Category is { } cat)
                            {
                                int contentRev = r.GetInt32(4);
                                int? verdictRev = r.IsDBNull(5) ? null : r.GetInt32(5);
                                string? cats = r.IsDBNull(6) ? null : r.GetString(6);
                                if (cats is null || verdictRev != contentRev || !ContainsCategory(cats, cat)) continue;
                            }
                            visible++;
                            if (top is not null)
                                top.Offer(r.IsDBNull(3) ? null : SqliteDates.ParseTicks(r.GetString(3)), r.GetInt64(0));
                        }
                    }
                    ids = scope == "live" ? top!.ExtractIds() : await SelectIdsSimple(conn, readTransaction, scopedPlatforms, scope, llmEnabled, owner, renderCap, ct);
                }
                else if (def.Union is null && def.Category is { } simpleCat)
                {
                    // Evaluate pure category views inside SQLite on covering index IX_Posts_FeedMembership,
                    // stopping the top-ID read right after RenderCap plus any PostedAt ties at the boundary.
                    var quotedCat = "\"" + simpleCat + "\"";
                    var top = scope == "live" ? new TopSelector(renderCap) : null;
                    const string catPredicate = "((INSTR(CategoriesJson, '\\') = 0 AND INSTR(CategoriesJson, $qcat) > 0) OR (INSTR(CategoriesJson, '\\') > 0 AND EXISTS (SELECT 1 FROM json_each(CategoriesJson) WHERE value = $cat)))";
                    foreach (var p in scopedPlatforms)
                    {
                        if (!platformStats.TryGetValue(p, out var st) || st.Total == st.Hidden) continue;
                        int platformCount;
                        await using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = readTransaction;
                            cmd.CommandText = $"SELECT COUNT(*) FROM Posts WHERE Platform = $p AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision AND {catPredicate}";
                            cmd.Parameters.AddWithValue("$p", p);
                            cmd.Parameters.AddWithValue("$qcat", quotedCat);
                            cmd.Parameters.AddWithValue("$cat", simpleCat);
                            platformCount = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
                        }
                        visible += platformCount;
                        if (top is null || platformCount == 0) continue;
                        await using (var cmd = conn.CreateCommand())
                        {
                            cmd.Transaction = readTransaction;
                            cmd.CommandText = $"SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision AND {catPredicate} ORDER BY PostedAt DESC";
                            cmd.Parameters.AddWithValue("$p", p);
                            cmd.Parameters.AddWithValue("$qcat", quotedCat);
                            cmd.Parameters.AddWithValue("$cat", simpleCat);
                            await using var r = await cmd.ExecuteReaderAsync(ct);
                            while (await r.ReadAsync(ct))
                            {
                                long? postedTicks = r.IsDBNull(1) ? null : SqliteDates.ParseTicks(r.GetString(1));
                                if (top.IsFull && Nullable.Compare(postedTicks, top.MinPostedTicks) < 0) break;
                                top.Offer(postedTicks, r.GetInt64(0));
                            }
                        }
                    }
                    ids = scope == "live" ? top!.ExtractIds() : await SelectIdsSimple(conn, readTransaction, scopedPlatforms, scope, llmEnabled, owner, renderCap, ct);
                }
                else
                {
                    // Prepare covering-index membership sets for rare and union views.
                    var leaves = def.Union is { } unionKeys ? unionKeys.Select(k => viewMap[k]).ToArray() : [def];
                    Dictionary<string, HashSet<long>>? authorPostIdsByView = null;
                    var rareLeaves = new List<ViewDefinition>();
                    foreach (var leaf in leaves)
                    {
                        if (leaf.Authors is not null)
                        {
                            authorPostIdsByView ??= new(StringComparer.Ordinal);
                            authorPostIdsByView[leaf.Key] = await LoadAuthorPostIds(conn, readTransaction, authorViews[leaf.Key], ct);
                        }
                        if (leaf.Rare is not null) rareLeaves.Add(leaf);
                    }
                    if (rareLeaves.Count > 0)
                        rareWhyByView = await ComputeRareMembership(conn, readTransaction, scopedPlatforms, rareLeaves, ct);

                    bool allLeavesRequireJudged = leaves.All(l => l.Category is not null || l.Rare is not null && llmEnabled);
                    bool anyLeafNeedsCategories = leaves.Any(l => l.Category is not null);
                    var top = scope == "live" ? new TopSelector(renderCap) : null;

                    // One covering-index scan over IX_Posts_FeedMembership per scoped platform evaluates membership,
                    // counts all visible matches, and selects the top RenderCap IDs without touching wide Posts rows.
                    foreach (var p in scopedPlatforms)
                    {
                        if (!platformStats.TryGetValue(p, out var st) || st.Total == st.Hidden) continue;
                        await using var cmd = conn.CreateCommand();
                        cmd.Transaction = readTransaction;
                        cmd.CommandText = allLeavesRequireJudged
                            ? (anyLeafNeedsCategories
                                ? "SELECT Id, PostedAt, CategoriesJson FROM Posts WHERE Platform = $p AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision ORDER BY PostedAt DESC"
                                : "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision ORDER BY PostedAt DESC")
                            : (anyLeafNeedsCategories
                                ? "SELECT Id, PostedAt, ContentRevision, VerdictContentRevision, CategoriesJson FROM Posts WHERE Platform = $p AND Hidden = 0 ORDER BY PostedAt DESC"
                                : "SELECT Id, PostedAt, ContentRevision, VerdictContentRevision, CategoriesJson IS NOT NULL FROM Posts WHERE Platform = $p AND Hidden = 0 ORDER BY PostedAt DESC");
                        cmd.Parameters.AddWithValue("$p", p);
                        await using var r = await cmd.ExecuteReaderAsync(ct);
                        bool platformDoneForTop = top is null;
                        while (await r.ReadAsync(ct))
                        {
                            long id = r.GetInt64(0);
                            bool judged;
                            string? cats = null;
                            bool catsRead = false;
                            if (allLeavesRequireJudged)
                            {
                                judged = true;
                            }
                            else
                            {
                                int contentRev = r.GetInt32(2);
                                int? verdictRev = r.IsDBNull(3) ? null : r.GetInt32(3);
                                bool hasCats = anyLeafNeedsCategories ? !r.IsDBNull(4) : r.GetInt32(4) != 0;
                                judged = hasCats && verdictRev == contentRev;
                            }

                            bool matched = false;
                            foreach (var leaf in leaves)
                            {
                                if (leaf.Category is { } cat)
                                {
                                    if (!judged) continue;
                                    if (leaf.Authors is not null && !authorPostIdsByView![leaf.Key].Contains(id)) continue;
                                    if (!catsRead) { cats = r.GetString(allLeavesRequireJudged ? 2 : 4); catsRead = true; }
                                    if (ContainsCategory(cats!, cat)) { matched = true; break; }
                                }
                                else if (leaf.Authors is not null)
                                {
                                    if (authorPostIdsByView![leaf.Key].Contains(id)) { matched = true; break; }
                                }
                                else if (leaf.Rare is not null)
                                {
                                    if ((!llmEnabled || judged) && rareWhyByView![leaf.Key].ContainsKey(id)) { matched = true; break; }
                                }
                            }
                            if (!matched) continue;
                            visible++;
                            if (!platformDoneForTop)
                            {
                                long? postedTicks = r.IsDBNull(1) ? null : SqliteDates.ParseTicks(r.GetString(1));
                                // Rows arrive in PostedAt DESC order within each platform: once the heap is full and
                                // PostedAt is strictly older than the heap minimum, no later row on this platform can enter.
                                if (top!.IsFull && Nullable.Compare(postedTicks, top.MinPostedTicks) < 0) platformDoneForTop = true;
                                else top.Offer(postedTicks, id);
                            }
                        }
                    }
                    ids = scope == "live" ? top!.ExtractIds() : await SelectIdsSimple(conn, readTransaction, scopedPlatforms, scope, llmEnabled, owner, renderCap, ct);
                }
            }
        }

        string? ExplainMatch(Post p, string key)
        {
            if (authorId is not null || key == "all") return null;
            var v = viewMap[key];
            if (v.Rare is not null) return rareWhyByView is not null && rareWhyByView.TryGetValue(key, out var map) && map.TryGetValue(p.Id, out var match) ? FormatRareWhy(match, v.Rare.WindowDays) : null;
            if (v.Union is { } members)
            {
                foreach (var member in members)
                {
                    var mv = viewMap[member];
                    bool hit; string? why = null;
                    if (mv.Category is { } cat) hit = p.Judged && ContainsCategory(p.CategoriesJson!, cat) && (mv.Authors is null || p.AuthorId is { } aid && authorViews[member].Contains(aid));
                    else if (mv.Authors is not null) hit = p.AuthorId is { } aid && authorViews[member].Contains(aid);
                    else if (mv.Rare is not null)
                    {
                        RareMatch rm = default;
                        hit = (!llmEnabled || p.Judged) && rareWhyByView is not null && rareWhyByView.TryGetValue(member, out var rmap) && rmap.TryGetValue(p.Id, out rm);
                        if (hit) why = FormatRareWhy(rm, mv.Rare.WindowDays);
                    }
                    else hit = false;
                    if (hit) return mv.Label + (why is null ? "" : " · " + why);
                }
            }
            return null;
        }

        Post[] selected;
        ILookup<long, Media> mediaByPost;
        ILookup<long, Like> likesByPost;
        ILookup<long, Feedback> feedbackByPost;
        if (ids.Length == 0)
        {
            selected = [];
            mediaByPost = Array.Empty<Media>().ToLookup(m => m.PostId);
            likesByPost = Array.Empty<Like>().ToLookup(l => l.PostId);
            feedbackByPost = Array.Empty<Feedback>().ToLookup(f => f.PostId);
        }
        else
        {
            // EF.Parameter sends the id list as one JSON parameter instead of one parameter per id: these four
            // lookups by primary key or PostId index are the one place where probing by id is the right plan.
            selected = (await db.Posts.AsNoTracking().Where(p => EF.Parameter(ids).Contains(p.Id)).ToArrayAsync(ct)).OrderByDescending(p => p.PostedAt).ThenByDescending(p => p.Id).ToArray();
            var media = await db.Media.AsNoTracking().Where(m => EF.Parameter(ids).Contains(m.PostId) && m.IsCurrent).OrderBy(m => m.Position).ThenBy(m => m.Id).ToArrayAsync(ct);
            var likes = await db.Likes.AsNoTracking().Where(l => EF.Parameter(ids).Contains(l.PostId)).ToArrayAsync(ct);
            var feedback = await db.Feedback.AsNoTracking().Where(f => EF.Parameter(ids).Contains(f.PostId)).ToArrayAsync(ct);
            mediaByPost = media.ToLookup(m => m.PostId); likesByPost = likes.ToLookup(l => l.PostId); feedbackByPost = feedback.ToLookup(f => f.PostId);
        }

        var cards = new Card[selected.Length];
        var tagsByPostId = new Dictionary<long, Tag[]>();
        for (int i = 0; i < selected.Length; i++)
        {
            var p = selected[i];
            var author = p.AuthorId is { } id ? authorMap.GetValueOrDefault(id) : null;
            var rules = new RuleContext(author, authors, instance, sharedRules);
            var why = scope == "live" ? ExplainMatch(p, view) : null;
            bool Shield(string gate) => rules.Shield(gate);
            Tag[]? parsedTags = null;
            Tag[] GetTags() => parsedTags ??= (tagsByPostId[p.Id] = Filters.Tags(p));
            var trail = new List<string>(5)
            {
                author?.IsFriend == true ? "friend/following"
                : Shield("audience") ? "explicit audience bypass"
                : instance.Config.Filters.Audience == "all_captured" ? "unrestricted captured audience"
                : GetTags().Any(t => rules.FriendTag(t, p.Platform)) ? "qualifying tagged friend under " + instance.Config.Filters.FriendTagException
                : "outside configured audience"
            };
            foreach (var gate in instance.Config.Filters.AlwaysShowBypasses) if (Shield(gate)) trail.Add(gate + " shield");
            trail.Add(p.CategoriesJson is null ? "not judged yet" : !p.Judged ? "content changed since judgment" : $"score {p.LlmScore}; categories {p.CategoriesJson}; {p.LlmReason}" + (p.PrefsVersion != version ? "; judged under an older policy/model configuration" : ""));
            if (why is not null) trail.Add("in this view as " + why);
            if (p.Hidden) trail.Add($"hidden by {p.HiddenBy}: {p.HiddenReason}");
            int up = 0, down = 0;
            foreach (var f in feedbackByPost[p.Id]) { if (f.Value > 0) up++; else if (f.Value < 0) down++; }
            cards[i] = new Card(p, author, mediaByPost[p.Id].ToArray(), likesByPost[p.Id].MaxBy(l => l.Id), up, down, why, trail.ToArray());
        }

        var protectedAuthors = authorViews.Values.SelectMany(ids => ids).ToHashSet();
        var cardsByAuthor = cards.Where(c => c.Post.AuthorId != null).ToLookup(c => c.Post.AuthorId!.Value);
        var repeatKeyCache = new Dictionary<long, string?>();
        string? GetRepeatKey(Card c)
        {
            if (!repeatKeyCache.TryGetValue(c.Post.Id, out var key))
                repeatKeyCache[c.Post.Id] = key = RepeatKey(c, tagsByPostId.GetValueOrDefault(c.Post.Id));
            return key;
        }
        var units = new List<FeedUnit>(cards.Length); var consumed = new HashSet<long>();
        foreach (var card in cards)
        {
            if (!consumed.Add(card.Post.Id)) continue; Card[] folded = [];
            // Separate source posts can contain exactly the same material (e.g. repeated
            // photo updates). Fold only complete byte-identical image sets, same author,
            // text and flags in a short burst. Keep identities and all actions intact.
            if (scope == "live" && card.Post.AuthorId is { } repeatAuthor && card.Post.PostedAt is { } repeatAt)
            {
                // Skip RepeatKey serialization unless this author has another unconsumed post in the 10-minute window.
                var windowStart = repeatAt.AddMinutes(-10);
                List<Card>? candidates = null;
                foreach (var c in cardsByAuthor[repeatAuthor])
                    if (!consumed.Contains(c.Post.Id) && c.Post.PostedAt <= repeatAt && c.Post.PostedAt >= windowStart)
                        (candidates ??= []).Add(c);
                if (candidates is not null && GetRepeatKey(card) is { } repeatKey)
                {
                    folded = candidates.Where(c => GetRepeatKey(c) == repeatKey).ToArray();
                    if (folded.Length > 0)
                    {
                        foreach (var c in folded) consumed.Add(c.Post.Id);
                        units.Add(new(card, folded, $"{folded.Length + 1} repeated posts")); continue;
                    }
                }
            }
            if (authorId is null && stack.MinPosts > 0 && card.Post.AuthorId is { } id && card.Post.PostedAt is { } date && !protectedAuthors.Contains(id))
            {
                var windowStart = date.AddDays(-stack.WindowDays);
                var burst = cardsByAuthor[id].Where(c => c.Post.PostedAt <= date && c.Post.PostedAt >= windowStart && !consumed.Contains(c.Post.Id)).ToArray();
                if (burst.Length + 1 >= stack.MinPosts) { folded = burst; foreach (var c in burst) consumed.Add(c.Post.Id); }
            }
            units.Add(new(card, folded, folded.Length == 0 ? null : $"{folded.Length + 1} posts " + (stack.WindowDays == 1 ? "today" : stack.WindowDays == 7 ? "this week" : $"in {stack.WindowDays} days")));
        }

        // Fetch the last 20 runs, any running runs, and the latest finished collect per platform using fast PK-ordered
        // scans instead of a correlated NOT EXISTS over the whole Runs table.
        var recentRuns = await db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(20).ToListAsync(ct);
        var runIds = new HashSet<long>(recentRuns.Select(r => r.Id));
        foreach (var r in await db.Runs.AsNoTracking().Where(r => r.Status == "running").ToArrayAsync(ct))
            if (runIds.Add(r.Id)) recentRuns.Add(r);
        foreach (var p in Platforms.All)
        {
            if (recentRuns.Any(r => r.Kind == "collect" && r.Platform == p && r.FinishedAt != null)) continue;
            if (await db.Runs.AsNoTracking().Where(r => r.Kind == "collect" && r.Platform == p && r.FinishedAt != null).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct) is { } latest && runIds.Add(latest.Id))
                recentRuns.Add(latest);
        }
        recentRuns.Sort((a, b) => b.Id.CompareTo(a.Id));

        var retryKey = $"model:{configHash}:judge:not_before";
        var kvRows = await db.Kv.AsNoTracking().Where(k => k.Key == "last_visit" || k.Key == retryKey).ToDictionaryAsync(k => k.Key, k => k.Value, ct);
        var states = await db.PlatformStates.AsNoTracking().ToArrayAsync(ct);
        var requests = await db.RunRequests.AsNoTracking().Where(r => r.Status == "pending" || r.Status == "claimed").ToArrayAsync(ct);
        var diagnosticsLevel = await Diagnostics(db, instance, platform, ct);

        var vdef = viewMap.GetValueOrDefault(view); var suffix = platform is null ? "" : " on " + platform;
        string empty = scope == "hidden" ? (owner is null ? "No hidden posts" : "No posts hidden by " + owner) + suffix + "." : scope == "unsorted" ? "Nothing is waiting for the model" + suffix + "." : vdef?.Category is not null && vdef.Authors is not null ? $"No posts from the people in the {vdef.Label} view judged as {instance.Taxonomy.Categories.Single(c => c.Key == vdef.Category).Label}{suffix} yet." : vdef?.Category is not null ? $"No posts judged as {vdef.Label}{suffix} yet." : vdef?.Rare is not null ? $"No rare voices{suffix} right now." : vdef?.Authors is not null ? $"No posts from the people in the {vdef.Label} view{suffix}." : vdef is not null ? $"Nothing in the {vdef.Label} view{suffix} yet." : platform is not null ? $"No posts on {platform} in the feed." : llmEnabled && unsorted > 0 ? "Nothing judged yet." : "The feed is empty. Nothing collected yet.";
        int friends = authors.Count(a => a.IsFriend && (platform is null ? instance.Config.Enabled(a.Platform) : a.Platform == platform));
        return new(instance, view, platform, scope, units.ToArray(), visible, hidden, unsorted, friends, known.ToArray(), states, recentRuns.ToArray(), requests, kvRows.GetValueOrDefault("last_visit"), empty, adsDropped, [], kvRows.GetValueOrDefault(retryKey), totalHidden, totalUnsorted, null, diagnosticsLevel) { Owner = owner };
    }

    // Uses ORDER BY PostedAt DESC without ", Id DESC" so SQLite satisfies the query from covering index
    // IX_Posts_FeedMembership without touching wide Posts rows, stopping as soon as PostedAt drops below
    // the cap-th boundary while keeping exact (PostedAt DESC, Id DESC) tie-breaking across ties and platforms.
    static async Task<long[]> SelectIdsSimple(SqliteConnection conn, SqliteTransaction tx, string[] platforms, string scope, bool llmEnabled, string? owner, int renderCap, CancellationToken ct)
    {
        var top = new TopSelector(renderCap);
        foreach (var p in platforms)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            if (scope == "hidden")
            {
                if (owner is null)
                    cmd.CommandText = "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 1 ORDER BY PostedAt DESC";
                else
                {
                    cmd.CommandText = "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 1 AND HiddenBy = $owner ORDER BY PostedAt DESC";
                    cmd.Parameters.AddWithValue("$owner", owner);
                }
            }
            else if (scope == "unsorted")
                cmd.CommandText = "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 0 AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision) ORDER BY PostedAt DESC";
            else if (llmEnabled)
                cmd.CommandText = "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision ORDER BY PostedAt DESC";
            else
                cmd.CommandText = "SELECT Id, PostedAt FROM Posts WHERE Platform = $p AND Hidden = 0 ORDER BY PostedAt DESC";
            cmd.Parameters.AddWithValue("$p", p);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                long? postedTicks = r.IsDBNull(1) ? null : SqliteDates.ParseTicks(r.GetString(1));
                if (top.IsFull && Nullable.Compare(postedTicks, top.MinPostedTicks) < 0) break;
                top.Offer(postedTicks, r.GetInt64(0));
            }
        }
        return top.ExtractIds();
    }

    static async Task<HashSet<long>> LoadAuthorPostIds(SqliteConnection conn, SqliteTransaction tx, HashSet<long> authorIds, CancellationToken ct)
    {
        var result = new HashSet<long>();
        if (authorIds.Count == 0) return result;
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT Id FROM Posts WHERE AuthorId IN (SELECT value FROM json_each($ids))";
        cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(authorIds));
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct)) result.Add(r.GetInt64(0));
        return result;
    }

    readonly record struct RareMatch(int Count, long GapTicks);
    static string FormatRareWhy(RareMatch m, int windowDays)
        => m.Count == 1
            ? m.GapTicks < 0 ? "first post we have seen" : "first post in " + Span(m.GapTicks / (double)TimeSpan.TicksPerDay)
            : $"{m.Count}{(m.Count == 2 ? "nd" : m.Count == 3 ? "rd" : "th")} post in {windowDays} days";

    // Streams (AuthorId, PostedAt, Id) from IX_Authors_Platform_IsFriend + IX_Posts_AuthorHistory in already-sorted
    // order (zero temp B-tree, zero wide row reads) and evaluates every rare view's sliding window in one pass.
    // Stores compact (Count, GapTicks) structs and formats badge text lazily only for the rendered cards.
    static async Task<Dictionary<string, Dictionary<long, RareMatch>>> ComputeRareMembership(SqliteConnection conn, SqliteTransaction tx, string[] platforms, List<ViewDefinition> rareViews, CancellationToken ct)
    {
        var results = rareViews.ToDictionary(v => v.Key, _ => new Dictionary<long, RareMatch>(), StringComparer.Ordinal);
        var buf = new List<(long Id, long Ticks)>(64);
        void Flush()
        {
            if (buf.Count == 0) return;
            // Verify monotonic ascending order (guarding against any mixed fractional-second timestamp formats).
            for (int i = 1; i < buf.Count; i++)
                if (buf[i].Ticks < buf[i - 1].Ticks) { buf.Sort((a, b) => a.Ticks.CompareTo(b.Ticks)); break; }
            foreach (var v in rareViews)
            {
                var rare = v.Rare!;
                var map = results[v.Key];
                long windowTicks = rare.WindowDays * TimeSpan.TicksPerDay;
                int left = 0, right = 0, firstSame = 0;
                for (int i = 0; i < buf.Count; i++)
                {
                    long ticks = buf[i].Ticks;
                    if (i > 0 && ticks != buf[i - 1].Ticks) firstSame = i;
                    while (right < buf.Count && buf[right].Ticks <= ticks) right++;
                    long cutoff = ticks - windowTicks;
                    while (left < right && buf[left].Ticks < cutoff) left++;
                    int count = right - left;
                    if (count <= rare.MaxPosts)
                        map[buf[i].Id] = new(count, count == 1 ? (firstSame == 0 ? -1L : ticks - buf[firstSame - 1].Ticks) : 0L);
                }
            }
            buf.Clear();
        }
        foreach (var p in platforms)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT p.AuthorId, p.PostedAt, p.Id
                FROM Authors a
                JOIN Posts p ON p.AuthorId = a.Id
                WHERE a.Platform = $p AND a.IsFriend = 1 AND p.PostedAt IS NOT NULL
                ORDER BY a.Id, p.PostedAt
                """;
            cmd.Parameters.AddWithValue("$p", p);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            long currentAuthor = -1;
            while (await r.ReadAsync(ct))
            {
                long aid = r.GetInt64(0);
                if (aid != currentAuthor) { Flush(); currentAuthor = aid; }
                buf.Add((r.GetInt64(2), SqliteDates.ParseTicks(r.GetString(1))));
            }
            Flush();
        }
        return results;
    }

    static bool ContainsCategory(string categoriesJson, string category)
    {
        if (categoriesJson.IndexOf('\\') >= 0)
            return (JsonSerializer.Deserialize<string[]>(categoriesJson) ?? []).Contains(category);
        int idx = 0;
        while ((idx = categoriesJson.IndexOf(category, idx, StringComparison.Ordinal)) >= 0)
        {
            int end = idx + category.Length;
            if (idx > 0 && categoriesJson[idx - 1] == '"' && end < categoriesJson.Length && categoriesJson[end] == '"') return true;
            idx = end;
        }
        return false;
    }

    // Keeps the newest `cap` (PostedAt DESC, Id DESC) entries in a bounded min-heap; null PostedAt sorts smallest.
    sealed class TopSelector(int cap)
    {
        readonly PriorityQueue<(long? PostedTicks, long Id), (long? PostedTicks, long Id)> heap = new(Math.Min(cap + 1, 256), AscendingOrder.Instance);
        public bool IsFull => heap.Count >= cap;
        public long? MinPostedTicks => heap.Peek().PostedTicks;
        public void Offer(long? postedTicks, long id)
        {
            var entry = (postedTicks, id);
            if (heap.Count < cap) { heap.Enqueue(entry, entry); return; }
            if (AscendingOrder.Instance.Compare(entry, heap.Peek()) > 0) heap.DequeueEnqueue(entry, entry);
        }
        public long[] ExtractIds()
        {
            int n = heap.Count;
            if (n == 0) return [];
            var result = new long[n];
            for (int i = n - 1; i >= 0; i--) result[i] = heap.Dequeue().Id;
            return result;
        }
        sealed class AscendingOrder : IComparer<(long? PostedTicks, long Id)>
        {
            public static readonly AscendingOrder Instance = new();
            public int Compare((long? PostedTicks, long Id) x, (long? PostedTicks, long Id) y)
            {
                int c = Nullable.Compare(x.PostedTicks, y.PostedTicks);
                return c != 0 ? c : x.Id.CompareTo(y.Id);
            }
        }
    }

    // The Manage dot needs scans of Posts, RawSnapshots and TimelineVisits that can cost more than the
    // rest of the feed on a slow disk. The answer per platform scope is reused for a minute: the dot is
    // an advisory pointer, and the Manage pages compute their own current detail.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime At, DiagnosticLevel Value)> diagnostics = new();
    async Task<DiagnosticLevel> Diagnostics(FeedDb db, InstanceSnapshot instance, string? platform, CancellationToken ct)
    {
        var key = platform ?? ""; if (diagnostics.TryGetValue(key, out var cached) && Clock.Now - cached.At < TimeSpan.FromMinutes(1)) return cached.Value;
        var value = await RecoveryQuery.Level(db, instance, platform, ct);
        if (value == DiagnosticLevel.None && await CoverageQuery.HasIssues(db, instance, platform, ct)) value = DiagnosticLevel.Notice;
        diagnostics[key] = (Clock.Now, value); return value;
    }
    static string? RepeatKey(Card card, Tag[]? cachedTags = null)
    {
        var p = card.Post; var images = card.Media.Where(m => m.Kind == "image").ToArray();
        MediaSource[] sources;
        try { sources = JsonSerializer.Deserialize<MediaSource[]>(p.MediaManifestJson) ?? []; } catch (JsonException) { return null; }
        if (sources.Length == 0 || sources.Any(m => m.Kind != "image") || images.Length != sources.Length || images.Any(m => string.IsNullOrEmpty(m.ContentHash)) || sources.Any(s => !images.Any(m => m.SourceKey == s.SourceKey))) return null;
        var tags = cachedTags ?? Filters.Tags(p);
        return JsonSerializer.Serialize(new { p.Platform, p.Text, p.StoryTitle, p.SharedAuthor, p.SharedText, p.SharedUrl, p.MemoryLabel, p.MemoryText, p.IsSponsored, p.IsSuggested, p.IsReel, p.IsEvent, tags = tags.Where(t => t.Kind == "with" || t.Url?.TrimEnd('/') != (card.Author?.Url ?? p.ObservedAuthorUrl)?.TrimEnd('/')).Select(t => new { t.Name, t.Url, t.Kind }).OrderBy(t => t.Name).ThenBy(t => t.Url), images = images.Select(m => m.ContentHash) });
    }
    static string Span(double days) => days >= 365 ? $"{(int)(days / 365)} years" : days >= 60 ? $"{(int)(days / 30)} months" : days >= 14 ? $"{(int)(days / 7)} weeks" : $"{Math.Max(1, (int)days)} days";
    // Only http(s) links are rendered as hrefs; a javascript: or data: value from a payload is dropped.
    public static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && u.Scheme is "https" or "http" ? value : null;
    public static string Relative(DateTime? date) { if (date is null) return "-"; var span = Clock.Now - date.Value; return span.TotalSeconds < 90 ? "just now" : span.TotalMinutes < 90 ? $"{(int)span.TotalMinutes} min" : span.TotalHours < 36 ? $"{(int)span.TotalHours} h" : span.TotalDays < 14 ? $"{(int)span.TotalDays} d" : date.Value.ToString("dd MMM yyyy"); }
}
