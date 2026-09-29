using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;
namespace Feed.Core.Application;

public static class IdentityStore
{
    // Caller owns a short ingest transaction. No IO apart from database operations.
    public static async Task<Author?> Resolve(FeedDb db, string platform, Person person, bool friend, CancellationToken ct, InstanceSnapshot? instance = null)
    {
        var refs = Identity.Refs(platform, person.Id, person.Url); if (refs.Length == 0) return null;
        var ids = await db.AuthorKeys.Where(k => k.Platform == platform && refs.Contains(k.Key)).Select(k => k.AuthorId).Distinct().ToListAsync(ct);
        var authors = await db.Authors.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        Author? author = null; var movedPosts = new List<Post>();
        if (authors.Count > 0)
        {
            var counts = await db.Posts.Where(p => p.AuthorId != null && ids.Contains(p.AuthorId.Value)).GroupBy(p => p.AuthorId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            author = authors.OrderByDescending(a => a.IsFriend).ThenByDescending(a => a.AvatarPath != null).ThenByDescending(a => counts.GetValueOrDefault(a.Id)).ThenBy(a => a.Id).First();
            foreach (var duplicate in authors.Where(a => a != author))
            {
                var posts = await db.Posts.Where(p => p.AuthorId == duplicate.Id).ToListAsync(ct);
                foreach (var post in posts) { post.AuthorId = author.Id; post.ContentRevision++; post.VisibilityRevision++; post.LlmAttemptedAt = post.SummaryAttemptedAt = null; post.LlmError = post.SummaryError = null; post.LlmFailures = post.SummaryFailures = 0; post.LlmTokenLimit = null; movedPosts.Add(post); }
                await db.Feedback.Where(p => p.AuthorId == duplicate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AuthorId, author.Id), ct);
                await db.TimelineVisits.Where(p => p.AuthorId == duplicate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AuthorId, author.Id), ct);
                await db.AuthorKeys.Where(p => p.AuthorId == duplicate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AuthorId, author.Id), ct);
                foreach (var key in db.AuthorKeys.Local.Where(k => k.AuthorId == duplicate.Id).ToArray()) db.Entry(key).State = EntityState.Detached;
                foreach (var target in await db.SweepTargets.Where(t => t.AuthorId == duplicate.Id).ToListAsync(ct))
                {
                    var same = await db.SweepTargets.FindAsync([target.Platform, target.CycleId, author.Id], ct);
                    if (same is null) db.SweepTargets.Add(new() { Platform = target.Platform, CycleId = target.CycleId, AuthorId = author.Id, Position = target.Position, State = target.State, RunId = target.RunId, VisitId = target.VisitId, AttemptedAt = target.AttemptedAt, Attempts = target.Attempts, RetryAt = target.RetryAt });
                    else { same.Position = Math.Min(same.Position, target.Position); same.Attempts = Math.Max(same.Attempts, target.Attempts); same.RetryAt = new[] { same.RetryAt, target.RetryAt }.Max(); if (target.State is "done" or "skipped") { same.State = target.State; same.VisitId = target.VisitId; } }
                    db.Remove(target);
                }
                author.IsFriend |= duplicate.IsFriend; author.AvatarPath ??= duplicate.AvatarPath; author.DisplayName ??= duplicate.DisplayName; author.Url ??= duplicate.Url; if (duplicate.FirstSeenAt < author.FirstSeenAt) author.FirstSeenAt = duplicate.FirstSeenAt;
                db.Remove(duplicate);
            }
        }
        author ??= person.Id is not null ? await db.Authors.SingleOrDefaultAsync(a => a.Platform == platform && a.PlatformAuthorId == person.Id, ct) : null;
        if (author is null) { author = new() { Platform = platform, PlatformAuthorId = person.Id }; db.Authors.Add(author); await db.SaveChangesAsync(ct); }
        author.DisplayName = person.Name ?? author.DisplayName; author.Url = person.Url ?? author.Url; author.IsFriend |= friend; author.LastSeenAt = Clock.Now;
        foreach (var r in refs) if (!await db.AuthorKeys.AnyAsync(k => k.Key == r, ct) && !db.AuthorKeys.Local.Any(k => k.Key == r)) db.AuthorKeys.Add(new() { Key = r, Platform = platform, AuthorId = author.Id });
        await db.SaveChangesAsync(ct); author.RefsJson = JsonSerializer.Serialize(await db.AuthorKeys.Where(k => k.AuthorId == author.Id).OrderBy(k => k.Key).Select(k => k.Key).ToArrayAsync(ct)); await db.SaveChangesAsync(ct);
        if (movedPosts.Count > 0)
        {
            var allAuthors = await db.Authors.ToListAsync(ct);
            foreach (var post in movedPosts)
            {
                var sources = JsonSerializer.Deserialize<MediaSource[]>(post.MediaManifestJson) ?? [];
                var media = await db.Media.Where(m => m.PostId == post.Id).ToListAsync(ct);
                post.ContentHash = Ingest.ContentHash(post, sources, media);
                if (instance is not null) Filters.Apply(post, new(author, allAuthors, instance));
            }
            await db.SaveChangesAsync(ct);
        }
        return author;
    }
}
public sealed record CompletionEvidence(string ListId, string? Cursor, string? NextCursor, bool Terminal, int? ExactTotal, bool ParseFailed = false);
public static class FriendsCompleteness
{
    public static bool Proven(IReadOnlyList<CompletionEvidence> pages, int people, bool interrupted, out string reason)
    {
        reason = "no proven completion evidence"; if (interrupted || people == 0 || pages.Count == 0) return false;
        if (pages.Any(p => p.ParseFailed) || pages.Select(p => p.ListId).Distinct().Count() != 1) { reason = "parse failure or mixed lists"; return false; }
        var totals = pages.Where(p => p.ExactTotal.HasValue).Select(p => p.ExactTotal!.Value).Distinct().ToArray(); if (totals.Length > 1 || totals.Length == 1 && totals[0] != people) { reason = "count mismatch"; return false; }
        string? cursor = null; var visited = new HashSet<string>();
        while (true)
        {
            var matches = pages.Where(p => p.Cursor == cursor).ToArray(); if (matches.Length != 1 || !visited.Add(cursor ?? "<first>")) { reason = "missing page or repeated cursor"; return false; }
            var page = matches[0]; if (page.Terminal) { reason = "continuous terminal pagination chain"; return visited.Count == pages.Count; }
            if (string.IsNullOrEmpty(page.NextCursor)) { reason = "broken cursor"; return false; } cursor = page.NextCursor;
        }
    }
    public static async Task<int> Prune(FeedDb db, string platform, IEnumerable<string> capturedRefs, bool proven, int imported, CancellationToken ct)
    {
        if (!proven || imported == 0) return 0; var refs = capturedRefs.ToHashSet(); var count = 0;
        foreach (var author in await db.Authors.Where(a => a.Platform == platform && a.IsFriend).ToListAsync(ct)) if (!Identity.Keys(author).Concat(Identity.Refs(platform, author.PlatformAuthorId, author.Url)).Any(refs.Contains)) { author.IsFriend = false; count++; }
        await db.SaveChangesAsync(ct); return count;
    }
}
