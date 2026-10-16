using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Feed.Core.Application;

public sealed record CleanupResult(int Posts, int Files, long Bytes, bool Preview)
{
    public override string ToString() => $"{(Preview ? "Preview" : "Deleted")}: {Posts} hidden posts, {Files} media files, {Bytes / 1e9:0.000} GB ({Bytes:N0} bytes).";
}

public sealed class StorageCleanup(InstancePaths paths, DbFactory factory)
{
    // Only a digest of platform identity survives deletion, to suppress replay/downloads.
    public static string DeletedKey(string platform, string id) => "deleted-post:" + Prompts.Sha(platform + "\n" + id);

    public async Task<CleanupResult> HiddenPosts(bool preview, CancellationToken ct)
    {
        using var processing = Hold("processing");
        using var facebook = Hold("facebook");
        using var instagram = Hold("instagram");
        using var ingestFb = Hold("ingest-facebook");
        using var ingestIg = Hold("ingest-instagram");
        await using var db = factory.Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var posts = await db.Posts.Where(p => p.Hidden).Select(p => new { p.Id, p.Platform, p.PlatformPostId }).ToListAsync(ct);
        var hiddenIds = db.Posts.Where(p => p.Hidden).Select(p => p.Id);
        var rows = await db.Media.Where(m => hiddenIds.Contains(m.PostId)).ToListAsync(ct);
        var protectedPaths = (await db.Media.Where(m => !hiddenIds.Contains(m.PostId) && m.Path != null).Select(m => m.Path!).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        int files = 0; long bytes = 0;
        foreach (var path in rows.Select(m => m.Path).OfType<string>().Distinct(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            if (protectedPaths.Contains(path)) continue;
            // Restrict deletion to post media, excluding avatars and diagnostic screenshots.
            var platform = path.StartsWith("media/facebook/", StringComparison.Ordinal) ? "facebook" : path.StartsWith("media/instagram/", StringComparison.Ordinal) ? "instagram" : null;
            if (platform is null) throw new IOException("Unexpected post media path; cleanup stopped.");
            var file = paths.SafeFile("media/" + platform, path[("media/" + platform + "/").Length..]);
            if (file is null) continue;
            var size = new FileInfo(file).Length;
            if (!preview) File.Delete(file);
            files++; bytes += size;
            if (!preview) { var folder = Path.GetDirectoryName(file)!; if (!Directory.EnumerateFileSystemEntries(folder).Any()) Directory.Delete(folder); }
        }
        if (!preview)
        {
            foreach (var post in posts) await db.Put(DeletedKey(post.Platform, post.PlatformPostId), "1", ct);
            await db.Feedback.Where(f => hiddenIds.Contains(f.PostId)).ExecuteDeleteAsync(ct);
            await db.Likes.Where(l => hiddenIds.Contains(l.PostId)).ExecuteDeleteAsync(ct);
            await db.Media.Where(m => hiddenIds.Contains(m.PostId)).ExecuteDeleteAsync(ct);
            await db.Posts.Where(p => p.Hidden).ExecuteDeleteAsync(ct);
            await transaction.CommitAsync(ct);
        }
        return new(posts.Count, files, bytes, preview);
    }

    public async Task<string> ResetDownloads(bool deletePosts, CancellationToken ct)
    {
        using var processing = Hold("processing");
        using var facebook = Hold("facebook");
        using var instagram = Hold("instagram");
        using var ingestFb = Hold("ingest-facebook");
        using var ingestIg = Hold("ingest-instagram");
        // Enumerate and validate the entire scope before touching files. Never follow links.
        if (new DirectoryInfo(paths.Get("media")).LinkTarget is not null) throw new IOException("Reset stopped: the media directory is a symbolic link.");
        var files = new List<string>();
        foreach (var folder in new[] { "raw", "media/facebook", "media/instagram", "media/diagnostics" })
            Inspect(new DirectoryInfo(paths.Get(folder)), files);
        await using var db = factory.Open();
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        foreach (var file in files) { ct.ThrowIfCancellationRequested(); File.Delete(file); }
        await db.RawDiagnostics.ExecuteDeleteAsync(ct);
        await db.CaptureRecoveryAttempts.ExecuteDeleteAsync(ct);
        await db.RawSnapshots.ExecuteDeleteAsync(ct);
        await db.TimelineVisits.ExecuteDeleteAsync(ct);
        await db.SweepTargets.ExecuteDeleteAsync(ct);
        await db.Kv.Where(k => k.Key.StartsWith("sweep:")).ExecuteDeleteAsync(ct);
        int count = 0;
        if (deletePosts)
        {
            await db.Feedback.ExecuteDeleteAsync(ct);
            await db.Likes.ExecuteDeleteAsync(ct);
            await db.Media.ExecuteDeleteAsync(ct);
            count = await db.Posts.ExecuteDeleteAsync(ct);
            await db.Kv.Where(k => k.Key.StartsWith("deleted-post:")).ExecuteDeleteAsync(ct);
            await db.PlatformStates.ExecuteUpdateAsync(s => s.SetProperty(p => p.LastOkRunAt, (DateTime?)null).SetProperty(p => p.LastRunFinishedAt, (DateTime?)null), ct);
        }
        else
        {
            await db.Media.ExecuteUpdateAsync(s => s.SetProperty(m => m.Path, (string?)null).SetProperty(m => m.ContentHash, (string?)null).SetProperty(m => m.PrunedAt, Feed.Core.Domain.Clock.Now), ct);
            await db.Posts.ExecuteUpdateAsync(s => s.SetProperty(p => p.RawRef, (string?)null).SetProperty(p => p.LatestRawRef, (string?)null), ct);
        }
        await transaction.CommitAsync(ct);
        return $"Deleted {files.Count} downloaded files. " + (deletePosts ? $"Deleted {count} posts; collection can start fresh." : "Post records kept; removed media will not automatically download again.") + " Configuration, preferences, people, login sessions and backgrounds kept.";
    }

    static void Inspect(DirectoryInfo directory, List<string> files)
    {
        if (directory.LinkTarget is not null) throw new IOException("Reset stopped: a download directory is a symbolic link.");
        if (!directory.Exists) return;
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null) throw new IOException("Reset stopped: a downloaded file or directory is a symbolic link.");
            if (entry is DirectoryInfo child) Inspect(child, files);
            else files.Add(entry.FullName);
        }
    }

    ResourceLock Hold(string name) => ResourceLock.Try(paths, name) ?? throw new ResourceBusyException("Feed work is active. Nothing was deleted; retry cleanup after it finishes.");
}
