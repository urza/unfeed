using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Feed.Core.Application;

// Blocked is the subset of Failed that waits for a parser upgrade: the raw is kept, its posts are stored, and
// nothing retries it by browser. Capture reports it apart from the failures that mean a broken run.
public sealed record IngestResult(int Found, int New, int Revised, int Media, int Failed, int Blocked = 0);
public sealed class Ingest(InstancePaths paths, DbFactory factory, MediaFiles media)
{
    public static bool FriendsDirectory(string dir) => Path.GetFileName(dir).StartsWith("friends-", StringComparison.Ordinal) && !Path.GetFileName(dir).StartsWith("friends-timelines-", StringComparison.Ordinal);
    public async Task Register(string platform, string file, long? runId, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(paths.Root, file); var info = new FileInfo(file); await using var db = factory.Open();
        await db.Database.ExecuteSqlAsync($"INSERT OR IGNORE INTO RawSnapshots (Platform,RunId,Path,Kind,CapturedAt,Parsed,Deleted,Bytes) VALUES ({platform},{runId},{relative},{(FriendsDirectory(info.DirectoryName!) ? "friends" : "feed")},{info.LastWriteTimeUtc},0,0,{info.Length})", ct);
        if (!System.IO.File.Exists(file + ".context") && !System.IO.File.Exists(file + ".visits")) return;
        if (!System.IO.File.Exists(file + ".visits") && await db.RawSnapshots.AnyAsync(r => r.Path == relative && r.CaptureToken != null, ct)) return;
        try {
            var context = CaptureContext.Read(file, await System.IO.File.ReadAllTextAsync(file, ct));
            var token = System.IO.File.Exists(file + ".visits") ? null : context?.Visit;
            await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(u => u.SetProperty(r => r.CaptureToken, token), ct);
        } catch (Exception e) when (e is IOException or FormatException) { /* Ingest records malformed evidence. No identity is inferred. */ }
    }
    public async Task Discover(string platform, CancellationToken ct)
    {
        var root = paths.Get("raw", platform); if (!Directory.Exists(root)) return;
        await using var db = factory.Open();
        var live = (await db.Runs.AsNoTracking().Where(r => r.Platform == platform && r.Status == "running" && r.Phase == "browser").Select(r => r.RawDir).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        // Load already-registered paths for this platform in one query so Discover skips the thousands of
        // previously registered files without opening a DbContext per file.
        var existing = await db.RawSnapshots.AsNoTracking().Where(r => r.Platform == platform).Select(r => new { r.Path, HasToken = r.CaptureToken != null }).ToDictionaryAsync(r => r.Path, r => r.HasToken, StringComparer.Ordinal, ct);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => Path.GetExtension(x) is ".json" or ".jsonl").Order())
        {
            if (live.Contains(Path.GetFileName(Path.GetDirectoryName(file)))) continue;
            var relative = Path.GetRelativePath(paths.Root, file);
            if (existing.TryGetValue(relative, out var hasToken) && !System.IO.File.Exists(file + ".visits") && (hasToken || !System.IO.File.Exists(file + ".context"))) continue;
            await Register(platform, file, null, ct);
        }
    }
    sealed class RuleCache(InstanceSnapshot instance)
    {
        List<Author>? authors;
        Dictionary<long, int>? indexById;
        ResolvedRules? rules;
        public async Task<RuleContext> For(FeedDb db, Author? author, CancellationToken ct)
        {
            if (authors is null)
            {
                await db.Database.OpenConnectionAsync(ct);
                var conn = (SqliteConnection)db.Database.GetDbConnection();
                var tx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
                var loaded = await SqliteDates.ReadAuthorsAsync(conn, tx, ct);
                authors = new List<Author>(loaded.Length + 16);
                authors.AddRange(loaded);
                indexById = new Dictionary<long, int>(loaded.Length + 16);
                for (int i = 0; i < loaded.Length; i++) indexById[loaded[i].Id] = i;
                rules = new ResolvedRules(authors, instance);
            }
            else if (rules is null)
            {
                rules = new ResolvedRules(authors, instance);
            }
            return new RuleContext(author, authors, instance, rules);
        }
        // Update one changed/inserted author in place instead of reloading the entire Authors table on every post.
        public void Upsert(Author author)
        {
            if (authors is null) return;
            var snapshot = new Author
            {
                Id = author.Id, Platform = author.Platform, PlatformAuthorId = author.PlatformAuthorId,
                DisplayName = author.DisplayName, Url = author.Url, IsFriend = author.IsFriend,
                AvatarPath = author.AvatarPath, RefsJson = author.RefsJson,
                FirstSeenAt = author.FirstSeenAt, LastSeenAt = author.LastSeenAt
            };
            if (indexById!.TryGetValue(author.Id, out int idx)) authors[idx] = snapshot;
            else { indexById[author.Id] = authors.Count; authors.Add(snapshot); }
            rules = null;
        }
        public void Invalidate() { authors = null; indexById = null; rules = null; }
    }
    public async Task<IngestResult> File(string platform, string file, InstanceSnapshot instance, bool offline, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(paths.Root, file); var info = new FileInfo(file); int found = 0, added = 0, revised = 0, files = 0;
        DateTime incomingCapturedAt;
        // Register the raw row and stamp AttemptedAt in one short open without re-reading the raw file body here;
        // File reads the body once below and commits context/capture token/diagnostics together.
        await using (var db = factory.Open())
        {
            await db.Database.ExecuteSqlAsync($"INSERT OR IGNORE INTO RawSnapshots (Platform,RunId,Path,Kind,CapturedAt,Parsed,Deleted,Bytes) VALUES ({platform},{(long?)null},{relative},{(FriendsDirectory(info.DirectoryName!) ? "friends" : "feed")},{info.LastWriteTimeUtc},0,0,{info.Length})", ct);
            await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.AttemptedAt, Clock.Now), ct);
            incomingCapturedAt = await db.RawSnapshots.AsNoTracking().Where(r => r.Path == relative).Select(r => r.CapturedAt).SingleAsync(ct);
        }
        try
        {
            var people = FriendsDirectory(Path.GetDirectoryName(file)!); var body = await System.IO.File.ReadAllTextAsync(file, ct);
            var context = CaptureContext.Read(file, body);
            var parsed = PayloadParser.Parse(platform, body, people, context);
            var hasVisitsSidecar = System.IO.File.Exists(file + ".visits");
            await using (var diagnosticsDb = factory.Open())
            {
                await using var tx = await diagnosticsDb.Database.BeginTransactionAsync(ct);
                var raw = await diagnosticsDb.RawSnapshots.SingleAsync(r => r.Path == relative, ct);
                raw.ContextJson = context is null ? null : JsonSerializer.Serialize(context);
                // Multi-visit deduplication cannot safely assign this capture to one person.
                raw.CaptureToken = hasVisitsSidecar ? null : context?.Visit;
                raw.ParserVersion = PayloadParser.Version;
                raw.Warning = parsed.Warnings.Count == 0 ? null : string.Join("; ", parsed.Warnings);
                await diagnosticsDb.RawDiagnostics.Where(d => d.RawSnapshotId == raw.Id).ExecuteDeleteAsync(ct);
                foreach (var d in parsed.Issues) diagnosticsDb.RawDiagnostics.Add(new() { RawSnapshotId = raw.Id, Code = d.Code, Fingerprint = d.Fingerprint, Path = d.Path, Impact = d.Impact, Evidence = d.Evidence, Root = d.Root, ParserVersion = PayloadParser.Version });
                await diagnosticsDb.SaveChangesAsync(ct); await tx.CommitAsync(ct);
            }
            if (people) foreach (var person in parsed.People)
            {
                await using var db = factory.Open(); Author? author;
                await using (var tx = await db.Database.BeginTransactionAsync(ct)) { author = await IdentityStore.Resolve(db, platform, person, true, ct, instance); await tx.CommitAsync(ct); } found++;
                if (author is not null && author.AvatarPath is null && person.Avatar is not null)
                {
                    var picture = await media.Image($"media/avatars/{platform}/{Identity.Safe(person.Id ?? author.Id.ToString())}{MediaFiles.Extension(person.Avatar)}", person.Avatar, offline, true, ct);
                    if (picture.Path is not null) { author.AvatarPath = picture.Path; await db.SaveChangesAsync(ct); }
                }
            }
            if (parsed.Posts.Count > 0)
            {
                // Batch-check deleted-post tombstones once for the whole capture instead of one Kv lookup per post.
                HashSet<string> deletedKeys;
                await using (var checkDb = factory.Open())
                {
                    var keys = parsed.Posts.Select(o => StorageCleanup.DeletedKey(o.Post.Platform, o.Post.PlatformPostId)).Distinct().ToArray();
                    deletedKeys = (await checkDb.Kv.AsNoTracking().Where(k => EF.Parameter(keys).Contains(k.Key)).Select(k => k.Key).ToArrayAsync(ct)).ToHashSet(StringComparer.Ordinal);
                }
                var ruleCache = new RuleCache(instance);
                foreach (var observation in parsed.Posts)
                {
                    found++;
                    if (deletedKeys.Contains(StorageCleanup.DeletedKey(observation.Post.Platform, observation.Post.PlatformPostId))) continue;
                    var result = await Store(observation, relative, incomingCapturedAt, instance, ruleCache, offline, ct);
                    added += result.New; revised += result.Revised; files += result.Media;
                }
            }
            // Safe observations are committed even when another record is incomplete.
            if (parsed.Diagnostics.Count > 0) throw new FormatException(string.Join("; ", parsed.Diagnostics));
            await using (var db = factory.Open()) await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.Parsed, true).SetProperty(r => r.ParsedAt, Clock.Now).SetProperty(r => r.Error, (string?)null).SetProperty(r => r.BlockedParserVersion, (int?)null), ct);
            return new(found, added, revised, files, 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            int? blocked = e is FormatException or JsonException ? PayloadParser.Version : null;
            await using var db = factory.Open(); await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.Error, e.Message).SetProperty(r => r.Parsed, false).SetProperty(r => r.BlockedParserVersion, blocked), ct); return new(found, added, revised, files, 1, blocked is null ? 0 : 1);
        }
    }
    // Changed content invalidates model output: clear the attempt state so the judge and the summary run again for the new revision.
    public static void InvalidateModel(Post p) { p.LlmAttemptedAt = p.SummaryAttemptedAt = null; p.LlmError = p.SummaryError = null; p.LlmFailures = p.SummaryFailures = 0; p.LlmTokenLimit = null; }
    public static string ContentHash(Post p, IEnumerable<MediaSource> sources, IEnumerable<Media> rows)
    {
        // Canonical content serializer v1: explicit stable field order. Fetch URLs and retention paths never enter it.
        // SQLite round-trips UTC wall-clock values without DateTimeKind; hash both forms identically.
        var obj = new { v = 1, p.AuthorId, p.ObservedAuthorName, p.ObservedAuthorUrl, PostedAt = p.PostedAt is { } date ? DateTime.SpecifyKind(date, DateTimeKind.Unspecified) : (DateTime?)null, p.Text, p.SharedAuthor, p.SharedText, p.SharedUrl, p.MemoryLabel, p.MemoryText, p.TagsJson, p.IsSponsored, p.IsSuggested, p.IsReel, p.IsEvent, media = sources.Select(s => new { s.Kind, s.SourceKey, bytes = s.Kind == "image" ? rows.FirstOrDefault(r => r.SourceKey == s.SourceKey)?.ContentHash : null }) };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((p.StoryTitle is null ? JsonSerializer.Serialize(obj) : JsonSerializer.Serialize(new { content = obj, p.StoryTitle })))));
    }
    async Task<IngestResult> Store(Observation o, string raw, DateTime incomingCapturedAt, InstanceSnapshot instance, RuleCache ruleCache, bool offline, CancellationToken ct)
    {
        long id; bool isNew, changed; string originalHash; int files = 0;
        await using (var db = factory.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var p = await db.Posts.SingleOrDefaultAsync(p => p.Platform == o.Post.Platform && p.PlatformPostId == o.Post.PlatformPostId, ct); isNew = p is null;
            if (p is not null)
            {
                var currentRef = p.LatestRawRef ?? p.RawRef;
                bool preserve = o.IsPartial && (p.IngestReadyAt is not null || currentRef != raw);
                if (!preserve && currentRef != raw)
                {
                    var current = currentRef is null ? null : await db.RawSnapshots.AsNoTracking().SingleOrDefaultAsync(r => r.Path == currentRef, ct);
                    preserve = currentRef is not null ? current is null || incomingCapturedAt <= current.CapturedAt : incomingCapturedAt < p.CapturedAt;
                }
                if (preserve) return new(1, 0, 0, 0, 0);
            }
            // Check provenance before resolving identities: a skipped replay must
            // not change author records, media, verdicts or post revisions either.
            var (author, rulesAffected, merged) = await IdentityStore.ResolveDetailed(db, o.Post.Platform, new(o.AuthorKey, o.AuthorName, o.AuthorUrl), false, ct, instance);
            if (merged) ruleCache.Invalidate();
            else if (rulesAffected && author is not null) ruleCache.Upsert(author);
            p ??= new() { Platform = o.Post.Platform, PlatformPostId = o.Post.PlatformPostId, RawRef = raw }; if (isNew) db.Add(p);
            originalHash = p.ContentHash; p.AuthorId = author?.Id;
            p.ObservedAuthorName = o.AuthorName; p.ObservedAuthorUrl = o.AuthorUrl; p.PostedAt = o.Post.PostedAt; p.Text = o.Post.Text; p.StoryTitle = o.Post.StoryTitle; p.Permalink = o.Post.Permalink; p.LikeRef = o.Post.LikeRef; p.IsSponsored = o.Post.IsSponsored; p.IsSuggested = o.Post.IsSuggested; p.IsReel = o.Post.IsReel; p.IsEvent = o.Post.IsEvent; p.SharedAuthor = o.Post.SharedAuthor; p.SharedText = o.Post.SharedText; p.SharedUrl = o.Post.SharedUrl; p.MemoryLabel = o.Post.MemoryLabel; p.MemoryText = o.Post.MemoryText; p.TagsJson = o.Post.TagsJson; p.LatestRawRef = raw; p.MediaManifestJson = JsonSerializer.Serialize(o.Media);
            var rows = isNew ? [] : await db.Media.Where(m => m.PostId == p.Id).ToListAsync(ct); var hash = ContentHash(p, o.Media, rows); changed = hash != originalHash;
            if (changed) { if (!isNew) p.ContentRevision++; p.IngestReadyAt = null; InvalidateModel(p); p.ContentHash = hash; Filters.Apply(p, await ruleCache.For(db, author, ct)); }
            foreach (var row in rows) row.IsCurrent = o.Media.Any(s => s.SourceKey == row.SourceKey);

            // When no media download can occur (text-only post or offline run), finalize media state and
            // IngestReadyAt in this same transaction (one fsync instead of two). Online runs with media
            // still commit first so no transaction ever spans an HTTP image download.
            if (o.Media.Count == 0 || offline)
            {
                if (o.Media.Count == 0)
                {
                    p.IngestReadyAt = Clock.Now;
                    await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                    return new(1, isNew ? 1 : 0, !isNew && changed ? 1 : 0, 0, 0);
                }
                if (isNew) { await db.SaveChangesAsync(ct); }
                files = await SettleMedia(db, p, rows, o, offline, ct);
                var finalHash = files > 0 ? ContentHash(p, o.Media, rows) : hash;
                if (!changed && finalHash != p.ContentHash) { p.ContentRevision++; InvalidateModel(p); }
                p.ContentHash = finalHash; p.IngestReadyAt = Clock.Now;
                await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
                return new(1, isNew ? 1 : 0, !isNew && (changed || finalHash != originalHash) ? 1 : 0, files, 0);
            }
            await db.SaveChangesAsync(ct); id = p.Id; await tx.CommitAsync(ct);
        }
        await using (var db = factory.Open())
        {
            var p = await db.Posts.SingleAsync(p => p.Id == id, ct); var rows = await db.Media.Where(m => m.PostId == id).ToListAsync(ct);
            files = await SettleMedia(db, p, rows, o, offline, ct);
            var finalHash = ContentHash(p, o.Media, rows);
            if (!changed && finalHash != p.ContentHash) { p.ContentRevision++; InvalidateModel(p); }
            p.ContentHash = finalHash; p.IngestReadyAt = Clock.Now; await db.SaveChangesAsync(ct);
            return new(1, isNew ? 1 : 0, !isNew && (changed || finalHash != originalHash) ? 1 : 0, files, 0);
        }
    }
    async Task<int> SettleMedia(FeedDb db, Post p, List<Media> rows, Observation o, bool offline, CancellationToken ct)
    {
        int pos = 0, files = 0;
        foreach (var source in o.Media)
        {
            pos++; var existing = rows.FirstOrDefault(m => m.SourceKey == source.SourceKey);
            if (existing is not null) { existing.IsCurrent = true; existing.OriginalUrl = source.Url; existing.Position = pos; if (existing.PrunedAt is not null || existing.Path is not null && System.IO.File.Exists(paths.Get(existing.Path)) || existing.Kind == "video") continue; }
            string suffix = rows.Any(m => m.Position == pos && m != existing) ? "-" + MediaFiles.Hash(Encoding.UTF8.GetBytes(source.SourceKey))[..12] : "";
            string rel = $"media/{p.Platform}/{Identity.Safe(p.PlatformPostId)}/{pos:00}{suffix}{(source.Kind == "video" ? ".mp4" : MediaFiles.Extension(source.Url))}";
            var row = existing ?? new Media { PostId = p.Id, Position = pos, Kind = source.Kind, OriginalUrl = source.Url, SourceKey = source.SourceKey };
            if (source.Kind == "image") {
                // Register identity before publishing bytes. A crash can recover the slot without assigning old bytes to a replacement source.
                if (existing is null && !offline) { db.Add(row); rows.Add(row); await db.SaveChangesAsync(ct); }
                var result = await media.Image(rel, source.Url, offline, false, ct);
                if (result.Path is null)
                {
                    Console.Error.WriteLine($"image {p.Platform}/#{p.Id} slot {pos}: {result.Error}");
                    // A glyph is a verdict on the file, and an offline run must not create network work: neither gets a new row.
                    // Any other failure keeps the slot with its retry state for Maintenance.Images. Dropping the row here lost
                    // the pictures of 34 posts for good when the proxy returned 502 for an hour on 2026-11-04.
                    if (result.Error == MediaFiles.Glyph || offline && existing is null) { if (!offline || existing is not null) { db.Remove(row); rows.Remove(row); } }
                    else if (!offline) { row.Error = result.Error; row.AttemptedAt = Clock.Now; row.DownloadAttempts++; }
                    if (!offline) await db.SaveChangesAsync(ct);
                    continue;
                }
                if (existing is null && offline) { db.Add(row); rows.Add(row); }
                row.Path = result.Path; row.ContentHash = result.Hash; row.Width = result.Info?.Width; row.Height = result.Info?.Height; row.Error = null; files++; }
            else { if (System.IO.File.Exists(paths.Get(rel))) { row.Path = rel; row.ContentHash = MediaFiles.Hash(await System.IO.File.ReadAllBytesAsync(paths.Get(rel), ct)); } else if (p.Hidden || offline) continue; }
            if (existing is null && source.Kind != "image") { db.Add(row); rows.Add(row); }
        }
        return files;
    }
}
