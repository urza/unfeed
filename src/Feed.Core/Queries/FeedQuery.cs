using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;

public sealed record Card(Post Post, Author? Author, Media[] Media, Like? Like, int Up, int Down, string? Why, string[] Trail);
public sealed record FeedUnit(Card Lead, Card[] Folded, string? Badge);
public sealed record FeedPage(InstanceSnapshot Instance, string View, string? Platform, string Scope, FeedUnit[] Items, int VisibleCount, int HiddenCount, int UnsortedCount, int Friends, string[] KnownPlatforms, PlatformState[] States, Run[] Runs, RunRequest[] Requests, string? LastVisit, string EmptyMessage, int AdsDropped, CoverageRow[] Coverage, string? ModelRetry, int TotalHidden, int TotalUnsorted, RecoveryIssue[]? Issues = null);
public sealed class FeedQuery(DbFactory factory)
{
    public async Task<FeedPage> Read(InstanceSnapshot instance, string view, string? platform, string scope, CancellationToken ct)
    {
        await using var db = factory.Open(); await db.Database.OpenConnectionAsync(ct);
        await using var readTransaction = ((Microsoft.Data.Sqlite.SqliteConnection)db.Database.GetDbConnection()).BeginTransaction(deferred: true);
        await db.Database.UseTransactionAsync(readTransaction, ct);
        var known = await db.Posts.Select(p => p.Platform).Distinct().OrderBy(p => p).ToArrayAsync(ct); if (platform is not null && !known.Contains(platform)) throw new ArgumentException("unknown platform");
        if (view != "all" && !instance.Taxonomy.Views.Any(v => v.Key == view)) throw new ArgumentException("unknown view");
        var authors = await db.Authors.AsNoTracking().ToArrayAsync(ct); var posts = await db.Posts.AsNoTracking().OrderByDescending(p => p.PostedAt).ThenByDescending(p => p.Id).ToArrayAsync(ct);
        var scoped = posts.Where(p => platform is null ? instance.Config.Enabled(p.Platform) : p.Platform == platform).ToArray();
        var authorMap = authors.ToDictionary(a => a.Id); var version = Prompts.Version(instance);
        var histories = posts.Where(p => p.AuthorId != null && p.PostedAt != null).GroupBy(p => p.AuthorId!.Value).ToDictionary(g => g.Key, g => g.Select(p => p.PostedAt!.Value).Order().ToArray());
        var authorViews = instance.Taxonomy.Views.Where(v => v.Authors is not null).ToDictionary(v => v.Key, v => v.Authors!.Value.SelectMany(e => Identity.Resolve(e, authors)).Select(a => a.Id).ToHashSet());
        var matches = new Dictionary<(long, string), (bool, string?)>();
        (bool Match, string? Why) Match(Post p, string key)
        {
            var cacheKey = (p.Id, key); if (matches.TryGetValue(cacheKey, out var cached)) return cached;
            return matches[cacheKey] = ComputeMatch(p, key);
        }
        (bool Match, string? Why) ComputeMatch(Post p, string key)
        {
            if (key == "all") return (!instance.Config.Llm.Enabled || p.Judged, null);
            var v = instance.Taxonomy.Views.Single(v => v.Key == key);
            if (v.Category is { } category) return (p.Judged && (JsonSerializer.Deserialize<string[]>(p.CategoriesJson!) ?? []).Contains(category) && (v.Authors is null || p.AuthorId is { } authorId && authorViews[key].Contains(authorId)), null);
            if (v.Authors is { } entries) return (p.AuthorId is { } id && authorViews[key].Contains(id), null);
            if (v.Rare is { } rare)
            {
                if (p.AuthorId is not { } id || !authorMap.TryGetValue(id, out var author) || !author.IsFriend || p.PostedAt is not { } date || instance.Config.Llm.Enabled && !p.Judged) return (false, null);
                var history = histories[id]; int end = Bound(history, date, true); int count = end - Bound(history, date.AddDays(-rare.WindowDays), false); if (count > rare.MaxPosts) return (false, null);
                int previousIndex = Bound(history, date, false) - 1; DateTime? previous = previousIndex < 0 ? null : history[previousIndex]; string fact = count == 1 ? previous is null ? "first post we have seen" : "first post in " + Span((date - previous.Value).TotalDays) : $"{count}{(count == 2 ? "nd" : count == 3 ? "rd" : "th")} post in {rare.WindowDays} days";
                return (true, fact);
            }
            foreach (var member in v.Union ?? []) { var result = Match(p, member); if (result.Match) return (true, instance.Taxonomy.Views.Single(x => x.Key == member).Label + (result.Why is null ? "" : " · " + result.Why)); }
            return (false, null);
        }
        var live = scoped.Where(p => !p.Hidden && Match(p, view).Match).ToArray(); var hidden = scoped.Count(p => p.Hidden); var unsorted = scoped.Count(p => !p.Hidden && !p.Judged);
        var selected = (scope == "hidden" ? scoped.Where(p => p.Hidden) : scope == "unsorted" ? scoped.Where(p => !p.Hidden && !p.Judged) : live).Take(instance.Config.Ui.RenderCap).ToArray(); var ids = selected.Select(p => p.Id).ToArray();
        var media = await db.Media.AsNoTracking().Where(m => ids.Contains(m.PostId) && m.IsCurrent).OrderBy(m => m.Position).ThenBy(m => m.Id).ToArrayAsync(ct); var likes = await db.Likes.AsNoTracking().Where(l => ids.Contains(l.PostId)).ToArrayAsync(ct); var feedback = await db.Feedback.AsNoTracking().Where(f => ids.Contains(f.PostId)).ToArrayAsync(ct);
        var mediaByPost = media.ToLookup(m => m.PostId); var likesByPost = likes.ToLookup(l => l.PostId); var feedbackByPost = feedback.ToLookup(f => f.PostId);
        var cards = selected.Select(p =>
        {
            var author = p.AuthorId is { } id ? authorMap.GetValueOrDefault(id) : null; var rules = new RuleContext(author, authors, instance); var why = scope == "live" ? Match(p, view).Why : null;
            var trail = new List<string> { author?.IsFriend == true ? "friend/following" : rules.Shield("audience") ? "explicit audience bypass" : instance.Config.Filters.Audience == "all_captured" ? "unrestricted captured audience" : Filters.Tags(p).Any(t => rules.FriendTag(t, p.Platform)) ? "qualifying tagged friend under " + instance.Config.Filters.FriendTagException : "outside configured audience" };
            foreach (var gate in instance.Config.Filters.AlwaysShowBypasses.Where(rules.Shield)) trail.Add(gate + " shield");
            trail.Add(p.CategoriesJson is null ? "not judged yet" : !p.Judged ? "content changed since judgment" : $"score {p.LlmScore}; categories {p.CategoriesJson}; {p.LlmReason}" + (p.PrefsVersion != version ? "; judged under an older policy/model configuration" : ""));
            if (why is not null) trail.Add("in this view as " + why); if (p.Hidden) trail.Add($"hidden by {p.HiddenBy}: {p.HiddenReason}");
            return new Card(p, author, mediaByPost[p.Id].ToArray(), likesByPost[p.Id].MaxBy(l => l.Id), feedbackByPost[p.Id].Count(f => f.Value > 0), feedbackByPost[p.Id].Count(f => f.Value < 0), why, trail.ToArray());
        }).ToArray();
        var protectedAuthors = instance.Taxonomy.Views.Where(v => v.Authors is not null).SelectMany(v => v.Authors!.Value).SelectMany(e => Identity.Resolve(e, authors)).Select(a => a.Id).ToHashSet();
        var cardsByAuthor = cards.Where(c => c.Post.AuthorId != null).ToLookup(c => c.Post.AuthorId!.Value);
        var units = new List<FeedUnit>(); var consumed = new HashSet<long>(); var stack = instance.Config.Ui.Stack;
        foreach (var card in cards)
        {
            if (!consumed.Add(card.Post.Id)) continue; Card[] folded = [];
            // Separate source posts can contain exactly the same material (e.g. repeated
            // photo updates). Fold only complete byte-identical image sets, same author,
            // text and flags in a short burst. Keep identities and all actions intact.
            if (scope == "live" && card.Post.AuthorId is { } repeatAuthor && card.Post.PostedAt is { } repeatAt && RepeatKey(card) is { } repeatKey)
            {
                folded = cardsByAuthor[repeatAuthor].Where(c => !consumed.Contains(c.Post.Id) && c.Post.PostedAt <= repeatAt && c.Post.PostedAt >= repeatAt.AddMinutes(-10) && RepeatKey(c) == repeatKey).ToArray();
                if (folded.Length > 0)
                {
                    foreach (var c in folded) consumed.Add(c.Post.Id);
                    units.Add(new(card, folded, $"{folded.Length + 1} repeated posts")); continue;
                }
            }
            if (stack.MinPosts > 0 && card.Post.AuthorId is { } id && card.Post.PostedAt is { } date && !protectedAuthors.Contains(id))
            {
                var burst = cardsByAuthor[id].Where(c => c.Post.PostedAt <= date && c.Post.PostedAt >= date.AddDays(-stack.WindowDays) && !consumed.Contains(c.Post.Id)).ToArray(); if (burst.Length + 1 >= stack.MinPosts) { folded = burst; foreach (var c in burst) consumed.Add(c.Post.Id); }
            }
            units.Add(new(card, folded, folded.Length == 0 ? null : $"{folded.Length + 1} posts " + (stack.WindowDays == 1 ? "today" : stack.WindowDays == 7 ? "this week" : $"in {stack.WindowDays} days")));
        }
        var vdef = instance.Taxonomy.Views.FirstOrDefault(v => v.Key == view); var suffix = platform is null ? "" : " on " + platform;
        string empty = scope == "hidden" ? "No hidden posts" + suffix + "." : scope == "unsorted" ? "Nothing is waiting for the model" + suffix + "." : vdef?.Category is not null && vdef.Authors is not null ? $"No posts from the people in the {vdef.Label} view judged as {instance.Taxonomy.Categories.Single(c => c.Key == vdef.Category).Label}{suffix} yet." : vdef?.Category is not null ? $"No posts judged as {vdef.Label}{suffix} yet." : vdef?.Rare is not null ? $"No rare voices{suffix} right now." : vdef?.Authors is not null ? $"No posts from the people in the {vdef.Label} view{suffix}." : vdef is not null ? $"Nothing in the {vdef.Label} view{suffix} yet." : platform is not null ? $"No posts on {platform} in the feed." : instance.Config.Llm.Enabled && unsorted > 0 ? "Nothing judged yet." : "The feed is empty. Nothing collected yet.";
        return new(instance, view, platform, scope, units.ToArray(), live.Length, hidden, unsorted, authors.Count(a => a.IsFriend && (platform is null ? instance.Config.Enabled(a.Platform) : a.Platform == platform)), known, await db.PlatformStates.AsNoTracking().ToArrayAsync(ct), await db.Runs.AsNoTracking().Where(r => r.Status == "running" || db.Runs.OrderByDescending(x => x.Id).Take(20).Select(x => x.Id).Contains(r.Id)).OrderByDescending(r => r.Id).ToArrayAsync(ct), await db.RunRequests.AsNoTracking().Where(r => r.Status == "pending" || r.Status == "claimed").ToArrayAsync(ct), await db.Get("last_visit", ct), empty, scoped.Count(p => p.Hidden && p.IsSponsored), await CoverageQuery.Read(db, instance, platform, ct), await db.Get($"model:{Prompts.ConfigurationHash(instance)}:judge:not_before", ct), posts.Count(p => p.Hidden), posts.Count(p => !p.Hidden && !p.Judged), await RecoveryQuery.Read(db, instance, platform, ct));
    }
    static string? RepeatKey(Card card)
    {
        var p = card.Post; var images = card.Media.Where(m => m.Kind == "image").ToArray();
        MediaSource[] sources;
        try { sources = JsonSerializer.Deserialize<MediaSource[]>(p.MediaManifestJson) ?? []; } catch (JsonException) { return null; }
        if (sources.Length == 0 || sources.Any(m => m.Kind != "image") || images.Length != sources.Length || images.Any(m => string.IsNullOrEmpty(m.ContentHash)) || sources.Any(s => !images.Any(m => m.SourceKey == s.SourceKey))) return null;
        return JsonSerializer.Serialize(new { p.Platform, p.Text, p.StoryTitle, p.SharedAuthor, p.SharedText, p.SharedUrl, p.MemoryLabel, p.MemoryText, p.IsSponsored, p.IsSuggested, p.IsReel, p.IsEvent, tags = Filters.Tags(p).Where(t => t.Kind == "with" || t.Url?.TrimEnd('/') != (card.Author?.Url ?? p.ObservedAuthorUrl)?.TrimEnd('/')).Select(t => new { t.Name, t.Url, t.Kind }).OrderBy(t => t.Name).ThenBy(t => t.Url), images = images.Select(m => m.ContentHash) });
    }
    static int Bound(DateTime[] dates, DateTime value, bool upper)
    {
        int low = 0, high = dates.Length;
        while (low < high) { int middle = low + (high - low) / 2; if (dates[middle] < value || upper && dates[middle] == value) low = middle + 1; else high = middle; }
        return low;
    }
    static string Span(double days) => days >= 365 ? $"{(int)(days / 365)} years" : days >= 60 ? $"{(int)(days / 30)} months" : days >= 14 ? $"{(int)(days / 7)} weeks" : $"{Math.Max(1, (int)days)} days";
    public static string Relative(DateTime? date) { if (date is null) return "-"; var span = Clock.Now - date.Value; return span.TotalSeconds < 90 ? "just now" : span.TotalMinutes < 90 ? $"{(int)span.TotalMinutes} min" : span.TotalHours < 36 ? $"{(int)span.TotalHours} h" : span.TotalDays < 14 ? $"{(int)span.TotalDays} d" : date.Value.ToString("dd MMM yyyy"); }
}
