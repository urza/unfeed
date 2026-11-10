using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

// One-time repair that runs at database initialization when the stored media key version is behind.
// Version 2: PayloadParser.SourceKey moved from a denylist of transient CDN parameters to an allowlist.
// Older keys still carried rotating parameters, so each recapture of an unchanged post created a new media
// identity, another download and another content revision. Rows are rekeyed from their stored URLs, duplicate
// identities of one post collapse to one row, and content hashes are recomputed with the new keys so that an
// unchanged post keeps its revision, verdict and summary. The revision is deliberately not advanced.
// Version 3: the key on a Meta CDN host is the asset filename. Version 2 keys still carried the host and the
// rendition, and the live instance held 1,216 groups of one asset under several keys (2026-11-10). The same
// pass runs again under the new rule, with two additions: a permalink: key survives (version 2 rekeyed those
// rows from the permalink URL, so every reel-only post gained a row and a revision at its next capture), and
// the widest rendition is kept when two renditions of one asset collapse.
public static class MediaKeys
{
    public const string Marker = "media:key-version"; public const string Version = "3";
    // A permalink video source stores the permalink as OriginalUrl and gets its key from the parser (permalink:<post id>),
    // so rekeying it from the URL would orphan it at the next capture. Version 2 did that; the second branch repairs it.
    static string Key(Post p, string kind, string url, string key) => key.StartsWith("permalink:", StringComparison.Ordinal) ? key : kind == "video" && url == p.Permalink ? "permalink:" + p.PlatformPostId : PayloadParser.SourceKey(url);
    public static async Task Normalize(FeedDb db, InstancePaths paths, CancellationToken ct)
    {
        if (await db.Get(Marker, ct) == Version) return;
        var ids = await db.Posts.Where(p => p.MediaManifestJson != "[]" || db.Media.Any(m => m.PostId == p.Id)).Select(p => p.Id).ToListAsync(ct);
        var superseded = new List<string>(); int removed = 0;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            for (int n = 0; n < ids.Count; n++)
            {
                var p = await db.Posts.SingleAsync(x => x.Id == ids[n], ct); var rows = await db.Media.Where(m => m.PostId == p.Id).OrderBy(m => m.Id).ToListAsync(ct);
                foreach (var row in rows) if (row.OriginalUrl is not null) row.SourceKey = Key(p, row.Kind, row.OriginalUrl, row.SourceKey);
                foreach (var group in rows.GroupBy(m => m.SourceKey).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList())
                {
                    // Prefer the row whose bytes are still on disk, then the widest (two renditions of one asset now share a key,
                    // and the newer row was the thumbnail in most live cases), then one with a known byte hash, then the newest.
                    var keep = group.OrderByDescending(m => m.Path is not null && File.Exists(paths.Get(m.Path))).ThenByDescending(m => m.Width ?? 0).ThenByDescending(m => m.ContentHash is not null).ThenByDescending(m => m.Id).First();
                    foreach (var loser in group.Where(m => m != keep)) { if (loser.Path is not null && loser.Path != keep.Path) superseded.Add(loser.Path); db.Remove(loser); rows.Remove(loser); removed++; }
                }
                MediaSource[] manifest; try { manifest = JsonSerializer.Deserialize<MediaSource[]>(p.MediaManifestJson) ?? []; } catch (JsonException) { manifest = []; }
                manifest = manifest.Select(s => s with { SourceKey = Key(p, s.Kind, s.Url, s.SourceKey) }).DistinctBy(s => s.SourceKey).ToArray();
                p.MediaManifestJson = JsonSerializer.Serialize(manifest);
                foreach (var row in rows) row.IsCurrent = manifest.Any(s => s.SourceKey == row.SourceKey);
                p.ContentHash = Ingest.ContentHash(p, manifest, rows);
                if (n % 500 == 499) { await db.SaveChangesAsync(ct); db.ChangeTracker.Clear(); }
            }
            await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
            await db.Put(Marker, Version, ct); await db.Put("media:key-migration", $"posts={ids.Count} duplicate rows removed={removed} at {Clock.Now:O}", ct); await tx.CommitAsync(ct);
        }
        // Duplicate bytes of a kept identity. Deleted after the commit, so an interruption leaves at most orphan files, never rows without files.
        var referenced = (await db.Media.Where(m => m.Path != null).Select(m => m.Path!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        foreach (var rel in superseded.Distinct().Where(r => !referenced.Contains(r) && r.StartsWith("media/", StringComparison.Ordinal)))
            if (paths.SafeFile("media", rel[6..]) is { } full && File.Exists(full)) File.Delete(full);
    }
}
