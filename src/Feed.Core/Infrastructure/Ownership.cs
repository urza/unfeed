using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Infrastructure;

public sealed partial class ResourceLock : IDisposable
{
    readonly FileStream stream;
    ResourceLock(FileStream stream) => this.stream = stream;
    public static ResourceLock? Try(InstancePaths paths, string name)
    {
        Directory.CreateDirectory(paths.Get("locks")); FileStream? stream = null;
        try
        {
            stream = new FileStream(paths.Get("locks", name + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            if (OperatingSystem.IsMacOS()) throw new PlatformNotSupportedException("Feed locks require Linux or Windows");
            if (OperatingSystem.IsLinux()) { if (Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 2 | 4) != 0) throw new IOException("resource locked"); } else stream.Lock(0, 1);
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { pid = Environment.ProcessId, started = ProcessIdentity.StartTime(Environment.ProcessId), at = Clock.Now })); stream.SetLength(0); stream.Write(bytes); stream.Flush(true);
            return new(stream);
        }
        catch (IOException) { stream?.Dispose(); return null; }
    }
    public static bool Busy(InstancePaths paths, string name) { using var held = Try(paths, name); return held is null; }
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "flock", SetLastError = true)] private static partial int Flock(int fd, int operation);
    public void Dispose() { stream.SetLength(0); stream.Flush(); if (OperatingSystem.IsLinux()) Flock(stream.SafeFileHandle.DangerousGetHandle().ToInt32(), 8); else if (!OperatingSystem.IsMacOS()) stream.Unlock(0, 1); stream.Dispose(); }
}
public static partial class ProcessIdentity
{
    public static DateTime StartTime(int pid)
    {
        if (!OperatingSystem.IsLinux()) { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime(); }
        // .NET derives Linux StartTime from a per-process boot-time estimate. Different
        // observers can disagree by fractions of a second, so exact comparison is unsafe.
        var stat = File.ReadAllText($"/proc/{pid}/stat");
        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        long startTicks = long.Parse(fields[19], System.Globalization.CultureInfo.InvariantCulture);
        long bootSeconds = long.Parse(File.ReadLines("/proc/stat").Single(line => line.StartsWith("btime ", StringComparison.Ordinal))[6..], System.Globalization.CultureInfo.InvariantCulture);
        long frequency = Sysconf(2); // Linux _SC_CLK_TCK
        if (frequency <= 0) throw new IOException("Cannot read OS process clock frequency");
        return DateTimeOffset.FromUnixTimeSeconds(bootSeconds).UtcDateTime.AddTicks(checked(startTicks * TimeSpan.TicksPerSecond / frequency));
    }
    [System.Runtime.InteropServices.LibraryImport("libc", EntryPoint = "sysconf")] private static partial nint Sysconf(int name);
    public static bool? Alive(int pid, DateTime started)
    {
        try { using var p = Process.GetProcessById(pid); return !p.HasExited && StartTime(pid) == DateTime.SpecifyKind(started, DateTimeKind.Utc); }
        catch (ArgumentException) { return false; } catch (InvalidOperationException) { return false; } catch (FileNotFoundException) { return false; } catch (DirectoryNotFoundException) { return false; } catch (IOException) { return null; } catch (UnauthorizedAccessException) { return null; } catch (System.ComponentModel.Win32Exception) { return null; }
    }
}
public sealed class RunLedger(DbFactory factory)
{
    public async Task<Run> Start(string kind, string? platform, string? mode = null, string trigger = "manual", long? requestId = null, string? token = null, CancellationToken ct = default, string? person = null)
    {
        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var start = ProcessIdentity.StartTime(Environment.ProcessId);
        if (requestId is not null)
        {
            var n = await db.RunRequests.Where(r => r.Id == requestId && r.Status == "claimed" && r.ClaimToken == token && r.ChildPid == null && r.Kind == kind && r.Platform == platform && (kind != "collect" || r.Mode == mode && r.Person == person)).ExecuteUpdateAsync(s => s.SetProperty(r => r.ChildPid, Environment.ProcessId).SetProperty(r => r.ChildStartedAt, start), ct);
            if (n != 1) throw new InvalidOperationException("Stale or already registered scheduler claim");
        }
        var run = new Run { Kind = kind, Platform = platform, Mode = mode, Trigger = trigger, ProcessPid = Environment.ProcessId, ProcessStartedAt = start, RequestId = requestId };
        db.Runs.Add(run); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return run;
    }
    public async Task Phase(long id, string phase, CancellationToken ct = default) { await using var db = factory.Open(); await db.Runs.Where(r => r.Id == id && r.Status == "running").ExecuteUpdateAsync(s => s.SetProperty(r => r.Phase, phase), ct); }
    public async Task Finish(Run run, string status, int exit, string? error = null, string? token = null)
    {
        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync();
        var row = await db.Runs.SingleAsync(r => r.Id == run.Id); row.Status = status; row.Phase = "finished"; row.FinishedAt = Clock.Now; row.Error = error; row.PostsFound = run.PostsFound; row.PostsNew = run.PostsNew; row.StatsJson = run.StatsJson; row.RawDir = run.RawDir;
        if (row.Kind == "collect" && row.Platform is { } p) { var state = await db.PlatformStates.FindAsync(p); if (state is null) { state = new() { Platform = p }; db.Add(state); } state.LastRunFinishedAt = Clock.Now; if (status is "ok" or "capped") { state.LastOkRunAt = Clock.Now; state.NeedsRelogin = false; } if (status == "checkpoint") state.NeedsRelogin = true; state.UpdatedAt = Clock.Now; }
        if (row.RequestId is { } requestId)
        {
            var req = await db.RunRequests.SingleOrDefaultAsync(r => r.Id == requestId && r.ClaimToken == token && r.Status == "claimed");
            if (req is not null) { req.Note = error; if (exit == 75) Reset(req); else { req.Status = exit == 0 ? "done" : "refused"; req.ExitCode = exit; req.FinishedAt = Clock.Now; } }
        }
        if (run.Kind == "process" && exit == 1 && error?.StartsWith("fatal:", StringComparison.Ordinal) == true) await db.Put("process:not_before", Clock.Now.AddMinutes(30).ToString("O"));
        await db.SaveChangesAsync(); await tx.CommitAsync();
    }
    public static void Reset(RunRequest r) { r.Status = "pending"; r.ClaimedAt = r.FinishedAt = r.ChildStartedAt = null; r.ClaimToken = null; r.ChildPid = r.ExitCode = null; }
    public async Task<List<string>> Recover(FeedConfig config, CancellationToken ct = default)
    {
        await using var db = factory.Open(); var warnings = new List<string>(); var cutoff = Clock.Now.AddMinutes(-2);
        foreach (var r in await db.Runs.Where(r => r.Status == "running" && r.StartedAt < cutoff).ToListAsync(ct))
            if (ProcessIdentity.Alive(r.ProcessPid, r.ProcessStartedAt) == false) { r.Status = "error"; r.Phase = "finished"; r.FinishedAt = Clock.Now; r.Error = "abandoned: the process ended without closing the run (crash, kill or a full disk)"; warnings.Add($"WARNING run #{r.Id} {r.Error}; closed as error");
                if (r.Kind == "collect" && r.Platform is { } platform) { var state = await db.PlatformStates.FindAsync([platform], ct); if (state is null) { state = new() { Platform = platform }; db.Add(state); } state.LastRunFinishedAt = Clock.Now; state.UpdatedAt = Clock.Now; }
                if (r.Kind == "process") await db.Put("process:not_before", Clock.Now.AddMinutes(30).ToString("O"), ct);
            }
            else if (ProcessIdentity.Alive(r.ProcessPid, r.ProcessStartedAt) is null) warnings.Add($"WARNING run #{r.Id}: process liveness unknown; ownership retained");
        foreach (var req in await db.RunRequests.Where(r => r.Status == "claimed").ToListAsync(ct))
        {
            if (req.ChildPid is null && req.ClaimedAt < cutoff) { Reset(req); req.Note = "recovered unregistered claim after startup grace"; }
            else if (req.ChildPid is { } pid && req.ChildStartedAt is { } started && ProcessIdentity.Alive(pid, started) == false) { req.Status = "refused"; req.FinishedAt = Clock.Now; req.ExitCode = 1; req.Note = "registered child ended without outcome"; }
        }
        var expiry = Clock.Now.AddMinutes(-config.Scheduler.RunRequestTtlMinutes);
        foreach (var req in await db.RunRequests.Where(r => (r.Kind == "collect" || r.Kind == "friends" || r.Kind == "login") && r.Status == "pending" && r.RequestedAt < expiry).ToListAsync(ct)) { req.Status = "expired"; req.FinishedAt = Clock.Now; req.Note = req.Kind + " request TTL elapsed"; }
        foreach (var target in await db.SweepTargets.Where(t => t.State == "visiting").ToListAsync(ct))
            if (!await db.Runs.AnyAsync(r => r.Id == target.RunId && r.Status == "running", ct)) { target.State = "pending"; target.RunId = null; }
        foreach (var platform in await db.Likes.Where(l => l.State == "pending" && l.AttemptedAt != null).Select(l => l.Platform).Distinct().ToListAsync(ct))
        {
            using var ownership = ResourceLock.Try(factory.Paths, platform); if (ownership is null) continue;
            await db.Likes.Where(l => l.Platform == platform && l.State == "pending" && l.AttemptedAt != null).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, "outcome unknown; inspect the original before retrying"), ct);
        }
        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);
        return warnings;
    }
}
