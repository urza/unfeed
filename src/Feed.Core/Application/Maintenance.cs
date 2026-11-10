using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public sealed record MediaTurn(int Selected, int Completed, int Failed);
public sealed class Maintenance(InstancePaths paths, DbFactory factory, HttpClient http)
{
    // Images are fetched at ingest, but a network, CDN or proxy outage must not lose the slot for good. The failed row keeps
    // its retry state and this pass completes it with the video rules: 30 minutes between attempts, pruned after three failures.
    public async Task<MediaTurn> Images(string[] platforms, int? limit, CancellationToken ct)
    {
        int completed = 0, failed = 0; await using var db = factory.Open(); var retry = Clock.Now.AddMinutes(-30); var files = new MediaFiles(paths, http);
        var rows = await db.Media.Where(m => m.Kind == "image" && m.IsCurrent && m.Path == null && m.PrunedAt == null && (m.AttemptedAt == null || m.AttemptedAt <= retry)).Join(db.Posts.Where(p => platforms.Contains(p.Platform)), m => m.PostId, p => p.Id, (m, p) => new { Media = m, Post = p }).OrderBy(x => x.Media.AttemptedAt ?? x.Media.CreatedAt).ThenBy(x => x.Media.Id).Take(limit ?? int.MaxValue).ToListAsync(ct);
        foreach (var item in rows)
        {
            var m = item.Media; var p = item.Post;
            m.AttemptedAt = Clock.Now; m.DownloadAttempts++; await db.SaveChangesAsync(ct);
            // Same file name ingest would have chosen: a retired row at this slot forces the keyed suffix.
            var shared = await db.Media.AnyAsync(x => x.PostId == p.Id && x.Position == m.Position && x.Id != m.Id, ct);
            var rel = $"media/{p.Platform}/{Identity.Safe(p.PlatformPostId)}/{m.Position:00}{(shared ? "-" + MediaFiles.Hash(Encoding.UTF8.GetBytes(m.SourceKey))[..12] : "")}{MediaFiles.Extension(m.OriginalUrl ?? "")}";
            var result = m.OriginalUrl is null ? (null, null, null, "no source url") : await files.Image(rel, m.OriginalUrl, false, false, ct);
            await db.Entry(m).ReloadAsync(ct); await db.Entry(p).ReloadAsync(ct);
            if (!m.IsCurrent || m.Path is not null) continue; // a concurrent ingest settled the slot meanwhile
            if (result.Path is null)
            {
                failed++; m.Error = result.Error;
                if (result.Error == MediaFiles.Glyph) db.Remove(m); else if (m.DownloadAttempts >= 3) m.PrunedAt = Clock.Now;
            }
            else
            {
                m.Path = result.Path; m.ContentHash = result.Hash; m.Width = result.Info?.Width; m.Height = result.Info?.Height; m.Error = null; completed++;
                // The judge reads pictures, so a late file is new content for the model.
                MediaSource[] manifest; try { manifest = JsonSerializer.Deserialize<MediaSource[]>(p.MediaManifestJson) ?? []; } catch (JsonException) { manifest = []; }
                var hash = Ingest.ContentHash(p, manifest, await db.Media.Where(x => x.PostId == p.Id).ToListAsync(ct));
                if (hash != p.ContentHash) { p.ContentRevision++; Ingest.InvalidateModel(p); p.ContentHash = hash; }
            }
            await db.SaveChangesAsync(ct);
        }
        return new(rows.Count, completed, failed);
    }
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
                    var file = paths.SafeFile("raw", snapshot.Path.StartsWith("raw/") ? snapshot.Path[4..] : snapshot.Path); if (file is not null) { files++; bytes += new FileInfo(file).Length; if (!dryRun) { File.Delete(file); foreach (var suffix in new[] { ".context", ".recovery", ".visits" }) File.Delete(file + suffix); } }
                    // .recovery stays in the list above: the model recovery writer is gone (plan 01), the overlays it left are not.
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
                    if (!expire) continue; var file = paths.SafeFile("media", x.Media.Path![6..]); if (file is not null) { files++; bytes += new FileInfo(file).Length; if (!dryRun) { File.Delete(file); foreach (var suffix in new[] { ".context", ".recovery", ".visits" }) File.Delete(file + suffix); } }
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
        var drive = new DriveInfo(paths.Root); await db.Put("disk:summary", $"media {Size("media") / 1e9:0.00} GB (videos {videos / 1e9:0.00} GB) · raw {Size("raw") / 1e9:0.00} GB · backups {Size("backups") / 1e9:0.00} GB · free {drive.AvailableFreeSpace / 1e9:0.00} GB", ct); await db.Put("disk:at", Clock.Now.ToString("O"), ct);
    }
    // Rotation keeps a week of daily copies and a month of Sunday copies, so a corruption noticed late still has a
    // good copy, and the last two pre-migration copies. Constants, not config: nobody asked for other numbers.
    public const int DailyBackups = 7, SundayBackups = 4, PreMigrationBackups = 2;
    // The daily backup takes no lock: VACUUM INTO reads one snapshot and never blocks a writer, and the processing
    // lock is often held by a child when the daily job runs. A failure is recorded for status and Manage and the
    // next day tries again; the Kv marker, not the file, is what Compact checks before its one-time VACUUM.
    public async Task<string> Backup(CancellationToken ct)
    {
        var folder = paths.Get("backups"); var target = Path.Combine(folder, $"feed-{Clock.Now:yyyyMMdd}.db"); var watch = Stopwatch.StartNew();
        await using var db = factory.Open();
        try
        {
            await Snapshot(db, target, ct); Rotate(folder);
            await db.Put("backup:last", Clock.Now.ToString("O"), ct); await db.Kv.Where(k => k.Key == "backup:error").ExecuteDeleteAsync(ct);
            return $"backup: {Path.GetFileName(target)} ok, {new FileInfo(target).Length / 1e6:0} MB in {watch.Elapsed.TotalSeconds:0.0} s";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            await db.Put("backup:error", $"{Clock.Now:O} {e.Message}", ct); return "backup failed: " + e.Message;
        }
    }
    // Shared by the daily backup and the pre-migration copy. The copy is written under a temporary name and takes
    // the target name only after integrity_check says ok, so a half-written or corrupt file never carries a backup
    // name. An existing target is kept: the second process of a parallel start must not redo or replace it.
    public static async Task Snapshot(FeedDb db, string target, CancellationToken ct)
    {
        // The live file is never a target. VACUUM INTO refuses an existing non-empty file anyway, but a check at the
        // boundary beats a SQLite error text, and no caller may ever rely on that refusal.
        if (Path.GetFullPath(target) == Path.GetFullPath(db.Paths.Database)) throw new IOException("backup target is the live database");
        if (File.Exists(target)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); var temp = target + "." + Environment.ProcessId + ".tmp";
        try
        {
            await db.Database.ExecuteSqlAsync($"VACUUM INTO {temp}", ct);
            await using (var copy = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={temp};Mode=ReadOnly;Pooling=False"))
            {
                await copy.OpenAsync(ct); using var check = copy.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
                if (await check.ExecuteScalarAsync(ct) is not "ok" and var result) throw new IOException("backup integrity check failed: " + result);
            }
            if (!File.Exists(target)) File.Move(temp, target);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    public static void Rotate(string folder)
    {
        if (!Directory.Exists(folder)) return;
        var daily = Directory.EnumerateFiles(folder, "feed-????????.db").Select(f => (File: f, Date: DateTime.TryParseExact(Path.GetFileNameWithoutExtension(f)[5..], "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) ? d : (DateTime?)null)).Where(x => x.Date != null).OrderByDescending(x => x.Date).ToArray();
        var keep = daily.Take(DailyBackups).Concat(daily.Where(x => x.Date!.Value.DayOfWeek == DayOfWeek.Sunday).Take(SundayBackups)).Select(x => x.File).ToHashSet();
        foreach (var x in daily) if (!keep.Contains(x.File)) File.Delete(x.File);
        // Migration names start with their timestamp, so the file name orders the pre-migration copies by age.
        foreach (var file in Directory.EnumerateFiles(folder, "pre-*.db").OrderDescending().Skip(PreMigrationBackups)) File.Delete(file);
    }
    public static async Task<string> BackupLine(InstancePaths paths, FeedDb db, CancellationToken ct)
    {
        var copies = Directory.Exists(paths.Get("backups")) ? Directory.EnumerateFiles(paths.Get("backups"), "*.db").Count() : 0;
        if (await db.Get("backup:error", ct) is { } error) return $"backup: FAILED {error}; {copies} copies in backups/";
        return await db.Get("backup:last", ct) is { } last ? $"backup: last {last}; {copies} copies in backups/" : "backup: none yet";
    }
    // Deleted raw rows outlive their files by months so that status and recovery still explain a recent capture,
    // then they go: at about 800 rows a day the table would otherwise grow for good. The files were deleted by the
    // prune before, so no run lookup depends on these rows.
    public async Task<string> Trim(FeedConfig c, bool dryRun, CancellationToken ct)
    {
        using var processing = ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("processing busy; retention deferred");
        if (c.RawRowsRetentionDays == 0) return "trim: raw rows retention is off";
        await using var db = factory.Open(); var cutoff = Clock.Now.AddDays(-c.RawRowsRetentionDays);
        var rows = db.RawSnapshots.Where(r => r.Deleted && r.CapturedAt <= cutoff); var diagnostics = db.RawDiagnostics.Where(d => rows.Any(r => r.Id == d.RawSnapshotId));
        var rowCount = await rows.CountAsync(ct); var diagnosticCount = await diagnostics.CountAsync(ct);
        if (!dryRun) { await diagnostics.ExecuteDeleteAsync(ct); await rows.ExecuteDeleteAsync(ct); }
        return $"trim: {(dryRun ? "dry-run" : "done")} deleted raw rows={rowCount}, diagnostics={diagnosticCount}; rows deleted more than {c.RawRowsRetentionDays} days ago by capture date";
    }
    // Free pages are given back by one VACUUM that also turns on incremental auto-vacuum; after that a daily
    // incremental_vacuum keeps the file small with no rewrite. The rewrite needs every writer parked behind the
    // locks and a good backup from the same day, because it touches the whole live file.
    public async Task<string> Compact(CancellationToken ct)
    {
        using var processing = ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("processing busy; compaction deferred");
        var ingest = Platforms.All.Select(p => ResourceLock.Try(paths, "ingest-" + p)).ToArray();
        try
        {
            if (ingest.Any(l => l is null)) throw new ResourceBusyException("ingest busy; compaction deferred");
            await using var db = factory.Open(); var watch = Stopwatch.StartNew(); var before = new FileInfo(paths.Database).Length;
            var mode = await db.Database.SqlQueryRaw<long>("SELECT auto_vacuum AS Value FROM pragma_auto_vacuum()").FirstAsync(ct); string step;
            if (mode == 2) { await db.Database.ExecuteSqlRawAsync("PRAGMA incremental_vacuum;", ct); step = "incremental vacuum"; }
            else if (await db.Get("backup:last", ct) is { } last && DateTime.Parse(last, null, System.Globalization.DateTimeStyles.RoundtripKind).Date == Clock.Now.Date)
            {
                // ANALYZE once here: the daily optimize below only refreshes statistics that exist.
                await db.Database.ExecuteSqlRawAsync("PRAGMA auto_vacuum = INCREMENTAL; VACUUM; ANALYZE;", ct); step = "converted to incremental auto-vacuum, analyzed";
            }
            else step = "conversion waits for a backup from today";
            // 0x10002: look at every table, not only those this fresh connection queried, and run ANALYZE where needed.
            await db.Database.ExecuteSqlRawAsync("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA optimize=0x10002;", ct);
            return $"compact: {step}; {before / 1e6:0} MB -> {new FileInfo(paths.Database).Length / 1e6:0} MB in {watch.Elapsed.TotalSeconds:0.0} s";
        }
        finally { foreach (var l in ingest) l?.Dispose(); }
    }
    sealed class OversizeException() : IOException("video exceeds configured size cap");
}
