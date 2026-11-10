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
        var idKey = Identity.IdRef(platform, person.Id);
        var owners = await db.AuthorKeys.Where(k => k.Platform == platform && refs.Contains(k.Key)).ToListAsync(ct);
        var ownerIds = owners.Select(k => k.AuthorId).Distinct().ToList();
        var authors = await db.Authors.Where(a => ownerIds.Contains(a.Id)).ToListAsync(ct);
        // Rows that are one account with this observation: the owner of the id key, and alias owners that carry no platform
        // id of their own (created from an observation without an id, or from a slug fallback). A slug or handle never joins
        // two rows that both have a platform id: a reclaimed handle would merge two people. The unique (Platform,
        // PlatformAuthorId) row joins too, so the id fill below cannot collide. Without an id in the observation the alias
        // owner is all the evidence there is.
        var candidates = authors.Where(a => owners.Any(k => k.AuthorId == a.Id && (k.Key == idKey || !Identity.IsPlatformId(a.PlatformAuthorId)))).ToList();
        if (idKey is not null && await db.Authors.SingleOrDefaultAsync(a => a.Platform == platform && a.PlatformAuthorId == person.Id, ct) is { } byId && candidates.All(a => a.Id != byId.Id)) candidates.Add(byId);
        if (candidates.Count == 0 && idKey is null && authors.Count > 0) candidates.Add(authors.MinBy(a => a.Id)!);
        Author? author = null; var movedPosts = new List<Post>(); var removed = new List<long>();
        if (candidates.Count > 0)
        {
            var counts = await db.Posts.Where(p => p.AuthorId != null && ownerIds.Contains(p.AuthorId.Value)).GroupBy(p => p.AuthorId!.Value).Select(g => new { Id = g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            // The row with the platform id is kept: the merged rows are the id-less ones, and the unique index allows one row per id.
            author = candidates.OrderByDescending(a => Identity.IsPlatformId(a.PlatformAuthorId)).ThenByDescending(a => a.IsFriend).ThenByDescending(a => a.AvatarPath != null).ThenByDescending(a => counts.GetValueOrDefault(a.Id)).ThenBy(a => a.Id).First();
            foreach (var duplicate in candidates.Where(a => a != author))
            {
                var posts = await db.Posts.Where(p => p.AuthorId == duplicate.Id).ToListAsync(ct);
                foreach (var post in posts) { post.AuthorId = author.Id; post.ContentRevision++; post.VisibilityRevision++; post.LlmAttemptedAt = post.SummaryAttemptedAt = null; post.LlmError = post.SummaryError = null; post.LlmFailures = post.SummaryFailures = 0; post.LlmTokenLimit = null; movedPosts.Add(post); }
                db.IdentityEvents.Add(new() { Platform = platform, Kind = "merge", Key = string.Join(" ", owners.Where(k => k.AuthorId == duplicate.Id).Select(k => k.Key)), ObservedId = person.Id, KeptAuthorId = author.Id, RemovedAuthorId = duplicate.Id,
                    DetailJson = JsonSerializer.Serialize(new { Name = duplicate.DisplayName, duplicate.PlatformAuthorId, duplicate.Url, duplicate.IsFriend, duplicate.AvatarPath, duplicate.FirstSeenAt, Keys = Identity.Keys(duplicate), PostIds = posts.Select(p => p.Id).ToArray() }) });
                await db.Feedback.Where(p => p.AuthorId == duplicate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.AuthorId, author.Id), ct);
                await db.RunRequests.Where(p => p.PersonAuthorId == duplicate.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.PersonAuthorId, author.Id), ct);
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
                db.Remove(duplicate); removed.Add(duplicate.Id);
            }
        }
        if (author is null) { author = new() { Platform = platform, PlatformAuthorId = person.Id }; db.Authors.Add(author); await db.SaveChangesAsync(ct); }
        // A numeric id is more evidence than a slug fallback or nothing, so it fills in; a known numeric id is never replaced.
        else if (!Identity.IsPlatformId(author.PlatformAuthorId) && Identity.IsPlatformId(person.Id)) author.PlatformAuthorId = person.Id;
        author.DisplayName = person.Name ?? author.DisplayName; author.Url = person.Url ?? author.Url; author.IsFriend |= friend; author.LastSeenAt = Clock.Now;
        // An alias that another id-bearing row owns stays there (its mute and close-friend entries keep their person); the log tells the owner.
        if (idKey is not null) foreach (var other in owners.Where(k => k.Key != idKey && k.AuthorId != author.Id && !removed.Contains(k.AuthorId)))
        {
            var conflict = await db.IdentityEvents.SingleOrDefaultAsync(e => e.Platform == platform && e.Kind == "alias_conflict" && e.Key == other.Key && e.ObservedId == person.Id, ct);
            if (conflict is null) db.IdentityEvents.Add(new() { Platform = platform, Kind = "alias_conflict", Key = other.Key, ObservedId = person.Id, KeptAuthorId = other.AuthorId, DetailJson = JsonSerializer.Serialize(new { person.Name, person.Url, AuthorId = author.Id }) });
            else { conflict.Count++; conflict.LastAt = Clock.Now; }
        }
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
    // The log with names for the operator: the kept row's current name, and the other side's name from the snapshot.
    public static async Task<IdentityEventRow[]> Events(FeedDb db, int take, string? kind = null, CancellationToken ct = default)
    {
        var events = await db.IdentityEvents.AsNoTracking().Where(e => kind == null || e.Kind == kind).OrderByDescending(e => e.LastAt).ThenByDescending(e => e.Id).Take(take).ToArrayAsync(ct);
        var ids = events.Select(e => e.KeptAuthorId).Distinct().ToList();
        var kept = await db.Authors.AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        return events.Select(e => new IdentityEventRow(e, kept.GetValueOrDefault(e.KeptAuthorId), JsonSerializer.Deserialize<JsonElement>(e.DetailJson).TryGetProperty("Name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null)).ToArray();
    }
}
public sealed record IdentityEventRow(IdentityEvent Event, Author? Kept, string? OtherName);
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
