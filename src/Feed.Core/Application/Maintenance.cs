using System.Diagnostics;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public sealed record MediaTurn(int Selected, int Completed, int Failed);
public sealed class Maintenance(InstancePaths paths, DbFactory factory, HttpClient http)
{
    public async Task<MediaTurn> Videos(InstanceSnapshot s, string[] platforms, int? limit, CancellationToken ct)
    {
        int completed = 0, failed = 0; await using var db = factory.Open(); var retry = Clock.Now.AddMinutes(-30);
        var rows = await db.Media.Where(m => m.Kind == "video" && m.IsCurrent && m.Path == null && m.PrunedAt == null && (m.AttemptedAt == null || m.AttemptedAt <= retry)).Join(db.Posts.Where(p => platforms.Contains(p.Platform)), m => m.PostId, p => p.Id, (m, p) => new { Media = m, Post = p }).Where(x => x.Post.Hidden || !s.Config.Llm.Enabled || x.Post.CategoriesJson != null && x.Post.VerdictContentRevision == x.Post.ContentRevision).OrderBy(x => x.Media.AttemptedAt ?? x.Media.CreatedAt).ThenBy(x => x.Media.Id).Take(limit ?? s.Config.Llm.Batch).ToListAsync(ct);
        foreach (var item in rows)
        {
            var m = item.Media; var p = item.Post;
            if (p.Hidden) { m.PrunedAt = Clock.Now; m.Error = "post hidden"; completed++; await db.SaveChangesAsync(ct); continue; }
            m.AttemptedAt = Clock.Now; m.DownloadAttempts++; await db.SaveChangesAsync(ct);
            var replacement = await db.Media.AnyAsync(x => x.PostId == p.Id && x.Position == m.Position && x.Id < m.Id, ct);
            var rel = $"media/{p.Platform}/{Identity.Safe(p.PlatformPostId)}/{m.Position:00}{(replacement ? "-" + Prompts.Sha(m.SourceKey)[..12] : "")}.mp4"; var full = paths.Get(rel); Directory.CreateDirectory(Path.GetDirectoryName(full)!); var temp = full + ".tmp"; var originalTemp = temp;
            try
            {
                long cap = s.Config.MaxVideoMb == 0 ? long.MaxValue : s.Config.MaxVideoMb * 1024L * 1024;
                var recovered = Directory.EnumerateFiles(Path.GetDirectoryName(full)!, Path.GetFileNameWithoutExtension(full) + ".*").FirstOrDefault(f => new[] { ".mp4", ".webm", ".mkv", ".mov" }.Contains(Path.GetExtension(f)) && new FileInfo(f).Length > 0);
                if (recovered is not null) { full = recovered; rel = Path.GetRelativePath(paths.Root, full); }
                else if (m.OriginalUrl is not null && m.OriginalUrl != p.Permalink)
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60)); using var response = await http.GetAsync(m.OriginalUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
                        if (response.Content.Headers.ContentLength > cap) throw new OversizeException();
                        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token); await using var output = File.Create(temp); var buffer = new byte[65536]; long total = 0; int n;
                        while ((n = await input.ReadAsync(buffer, timeout.Token)) > 0) { total += n; if (total > cap) throw new OversizeException(); await output.WriteAsync(buffer.AsMemory(0, n), timeout.Token); }
                    }
                    catch (Exception e) when ((e is HttpRequestException or TaskCanceledException) && !ct.IsCancellationRequested && p.Permalink is not null) { temp = await DownloadTool(p, temp, cap, ct); }
                }
                else temp = await DownloadTool(p, temp, cap, ct);
                await db.Entry(m).ReloadAsync(ct); await db.Entry(p).ReloadAsync(ct);
                if (!m.IsCurrent || p.Hidden) { File.Delete(temp); m.PrunedAt = Clock.Now; m.Error = "source superseded or post hidden"; }
                else {
                    if (recovered is null) { if (temp != originalTemp) { rel = Path.ChangeExtension(rel, Path.GetExtension(temp)); full = paths.Get(rel); } File.Move(temp, full, false); }
                    m.Path = rel; m.ContentHash = MediaFiles.Hash(await File.ReadAllBytesAsync(full, ct)); (m.Width, m.Height) = await VideoSize(full, ct); m.Error = null; completed++;
                }
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested) { failed++; m.Error = e.Message; if (m.DownloadAttempts >= 3 || e is OversizeException or System.ComponentModel.Win32Exception) m.PrunedAt = Clock.Now; }
            finally { foreach (var partial in Directory.EnumerateFiles(Path.GetDirectoryName(originalTemp)!, Path.GetFileName(originalTemp) + "*")) File.Delete(partial); }
            await db.SaveChangesAsync(ct);
        }
        return new(rows.Count, completed, failed);
    }
    async Task<string> DownloadTool(Post p, string output, long cap, CancellationToken ct)
    {
        if (p.Permalink is null) throw new IOException("no permalink for video fallback");
        var cookie = paths.Get("profiles", p.Platform, "cookies.txt"); var copy = Path.Combine(Path.GetTempPath(), "feed-cookies-" + Guid.NewGuid().ToString("N"));
        try
        {
            if (File.Exists(cookie)) File.Copy(cookie, copy);
            var start = new ProcessStartInfo("yt-dlp") { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            var args = new List<string> { "--no-playlist", "--no-progress", "--max-filesize", cap.ToString(), "-f", "best[ext=mp4]/best", "--print", "after_move:filepath", "-o", output + ".%(ext)s" }; if (File.Exists(copy)) args.AddRange(["--cookies", copy]); args.Add(p.Permalink); foreach (var a in args) start.ArgumentList.Add(a);
            using var process = Process.Start(start)!; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(300)); var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try {
                await process.WaitForExitAsync(timeout.Token); var printed = await stdout; var error = await stderr;
                var result = printed.Split('\n', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim();
                if (process.ExitCode != 0 || result is null || !Path.GetFullPath(result).StartsWith(Path.GetFullPath(output) + ".", StringComparison.Ordinal) || !File.Exists(result)) throw new IOException("yt-dlp: " + error);
                if (new FileInfo(result).Length > cap) throw new OversizeException(); return result;
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } }

        }
        finally { if (File.Exists(copy)) File.Delete(copy); }
    }
    static async Task<(int? Width, int? Height)> VideoSize(string path, CancellationToken ct)
    {
        try {
            var start = new ProcessStartInfo("ffprobe") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height", "-of", "csv=s=x:p=0", path }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!; using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync(timeout.Token); var parts = (await output).Trim().Split('x'); if (parts.Length == 2 && int.TryParse(parts[0], out var w) && int.TryParse(parts[1], out var h)) return (w, h); }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); await Task.WhenAll(output, error); }
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException || e is OperationCanceledException && !ct.IsCancellationRequested) { }
        return (null, null);
    }
    public async Task<string> Prune(FeedConfig c, bool raw, bool dryRun, DateTime? before, CancellationToken ct)
    {
        using var processing = ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("processing busy; retention deferred");
        int files = 0; long bytes = 0;
        foreach (var platform in Platforms.All)
        {
            using var ingest = ResourceLock.Try(paths, "ingest-" + platform); if (ingest is null) continue;
            await using var db = factory.Open();
            if (raw)
            {
                if (c.RawRetentionDays == 0 && before is null) continue;
                var snapshots = await db.RawSnapshots.Where(r => r.Platform == platform && r.Parsed && !r.Deleted).ToListAsync(ct);
                foreach (var snapshot in snapshots)
                {
                    var run = snapshot.RunId is null ? null : await db.Runs.FindAsync([snapshot.RunId], ct); if (run?.Status == "running") continue;
                    var cutoff = before ?? Clock.Now.AddDays(-c.RawRetentionDays * (run?.Status is "capped" or "checkpoint" ? 2 : 1)); if (snapshot.CapturedAt > cutoff) continue;
                    var file = paths.SafeFile("raw", snapshot.Path.StartsWith("raw/") ? snapshot.Path[4..] : snapshot.Path); if (file is not null) { files++; bytes += new FileInfo(file).Length; if (!dryRun) File.Delete(file); }
                    if (!dryRun) snapshot.Deleted = true;
                }
                if (!dryRun) await db.SaveChangesAsync(ct);
                var runRoot = paths.Get("raw", platform);
                if (Directory.Exists(runRoot)) foreach (var directory in Directory.EnumerateDirectories(runRoot))
                {
                    var name = Path.GetFileName(directory); var run = await db.Runs.Where(r => r.Platform == platform && r.RawDir == name).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
                    if (run is null || run.Status == "running" || run.FinishedAt > (before ?? Clock.Now.AddDays(-c.RawRetentionDays * (run.Status is "capped" or "checkpoint" ? 2 : 1)))) continue;
                    var prefix = $"raw/{platform}/{name}/";
                    if (await db.RawSnapshots.AnyAsync(r => r.Path.StartsWith(prefix) && !r.Deleted, ct)) continue;
                    if (!dryRun) { Directory.Delete(directory, true); var diagnostics = paths.Get("media", "diagnostics", platform, name); if (Directory.Exists(diagnostics)) Directory.Delete(diagnostics, true); }
                }
            }
            else
            {
                var rows = await db.Media.Join(db.Posts.Where(p => p.Platform == platform), m => m.PostId, p => p.Id, (m, p) => new { Media = m, Post = p }).Where(x => x.Media.Path != null).ToListAsync(ct);
                foreach (var x in rows)
                {
                    bool hidden = x.Post.Hidden && x.Post.HiddenAt is not null;
                    bool expire = hidden && c.HiddenMediaRetentionDays > 0 && x.Post.HiddenAt <= Clock.Now.AddDays(-c.HiddenMediaRetentionDays) || x.Media.Kind == "video" && (hidden && c.HiddenVideoRetentionDays > 0 && x.Post.HiddenAt <= Clock.Now.AddDays(-c.HiddenVideoRetentionDays) || c.VideoRetentionDays > 0 && x.Post.PostedAt <= Clock.Now.AddDays(-c.VideoRetentionDays));
                    if (!expire) continue; var file = paths.SafeFile("media", x.Media.Path![6..]); if (file is not null) { files++; bytes += new FileInfo(file).Length; if (!dryRun) File.Delete(file); }
                    if (!dryRun) { x.Media.Path = null; x.Media.PrunedAt = Clock.Now; x.Media.Error = "retention"; }
                }
                if (!dryRun) await db.SaveChangesAsync(ct);
                var mediaRoot = paths.Get("media", platform);
                var postFolders = (await db.Posts.Where(p => p.Platform == platform).Select(p => p.PlatformPostId).ToListAsync(ct)).Select(Identity.Safe).ToHashSet();
                if (Directory.Exists(mediaRoot)) foreach (var directory in Directory.EnumerateDirectories(mediaRoot))
                {
                    if (new DirectoryInfo(directory).LinkTarget is not null || postFolders.Contains(Path.GetFileName(directory)) || Directory.GetLastWriteTimeUtc(directory) > Clock.Now.AddDays(-1)) continue;
                    foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) { files++; bytes += new FileInfo(file).Length; }
                    if (!dryRun) Directory.Delete(directory, true);
                }
            }
        }
        if (!dryRun) await Measure(ct); return $"{(raw ? "raw" : "media")} prune: {(dryRun ? "dry-run" : "done")} eligible files={files}, bytes={bytes}; retention scope from instance, oldest eligible files";
    }
    public async Task Measure(CancellationToken ct)
    {
        long Size(string folder) => Directory.Exists(paths.Get(folder)) ? Directory.EnumerateFiles(paths.Get(folder), "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length) : 0;
        await using var db = factory.Open(); var videoPaths = await db.Media.Where(m => m.Kind == "video" && m.Path != null).Select(m => m.Path!).ToListAsync(ct); var videos = videoPaths.Where(p => File.Exists(paths.Get(p))).Sum(p => new FileInfo(paths.Get(p)).Length);
        var drive = new DriveInfo(paths.Root); await db.Put("disk:summary", $"media {Size("media") / 1e9:0.00} GB (videos {videos / 1e9:0.00} GB) · raw {Size("raw") / 1e9:0.00} GB · free {drive.AvailableFreeSpace / 1e9:0.00} GB", ct); await db.Put("disk:at", Clock.Now.ToString("O"), ct);
    }
    sealed class OversizeException() : IOException("video exceeds configured size cap");
}
