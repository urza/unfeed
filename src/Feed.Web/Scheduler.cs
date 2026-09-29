using System.Diagnostics;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Web;
public sealed class Scheduler(InstancePaths paths, InstanceFiles files, DbFactory factory, ILogger<Scheduler> log, Func<string>? cliResolver = null) : BackgroundService
{
    public DateTime? LastTick { get; private set; } public string? Error { get; private set; } public string? Cli { get; private set; }
    bool startup = true;
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var owner = ResourceLock.Try(paths, "scheduler") ?? throw new InvalidOperationException("Another scheduler owns this instance");
        while (!stoppingToken.IsCancellationRequested) { try { await Tick(stoppingToken); Error = null; } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; } catch (Exception e) { Error = e.Message; log.LogError(e, "Scheduler tick failed"); } await Task.Delay(5000, stoppingToken); }
    }
    public static int Jitter(string p, string mode, string slot, string day, int max) { if (max == 0) return 0; uint hash = 17; foreach (var c in $"{p}|{mode}|{slot}|{day}") hash = unchecked(hash * 31 + c); return (int)(hash % ((uint)max + 1)); }
    public async Task Tick(CancellationToken ct)
    {
        var snapshot = files.Refresh(); var c = snapshot.Config; LastTick = Clock.Now;
        foreach (var warning in await new RunLedger(factory).Recover(c, ct)) log.LogWarning("{Warning}", warning);
        if (!c.Scheduler.Enabled) return;
        await using var db = factory.Open();
        var states = await db.PlatformStates.AsNoTracking().ToDictionaryAsync(p => p.Platform, ct);
        bool Relogin(string p) => states.GetValueOrDefault(p)?.NeedsRelogin == true;
        async Task<bool> BrowserBusy(string p) => ResourceLock.Busy(paths, p) || await db.RunRequests.AnyAsync(r => r.Platform == p && r.Status == "claimed" && r.Kind != "process" && (r.ChildPid == null || db.Runs.Any(x => x.RequestId == r.Id && x.Status == "running" && (x.Phase == "starting" || x.Phase == "browser"))), ct);
        foreach (var p in Platforms.All)
        {
            if (!c.Platform(p).Likeback) { await db.RunRequests.Where(r => r.Platform == p && r.Kind == "like" && r.Status == "pending").ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "refused").SetProperty(r => r.FinishedAt, Clock.Now).SetProperty(r => r.Note, "like-back disabled"), ct); await db.Likes.Where(l => l.Platform == p && l.State == "pending" && l.AttemptedAt == null).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, $"like-back is not enabled for {p} (platforms.{p}.likeback)"), ct); }
            else if (await db.Likes.AnyAsync(l => l.Platform == p && l.State == "pending", ct)) await Actions.EnsureRequest(db, "like", p, ct: ct);
        }
        foreach (var request in await db.RunRequests.Where(r => r.Status == "pending" && r.Kind != "process").OrderBy(r => r.Id).ToListAsync(ct))
        {
            var p = request.Platform!;
            if (!Platforms.All.Contains(p) || request.Kind is not ("collect" or "like") || request.Kind == "collect" && !Platforms.Modes.Contains(Platforms.Mode(request.Mode ?? "")))
            { request.Status = "refused"; request.FinishedAt = Clock.Now; request.Note = "unknown platform, kind or mode"; continue; }
            if (Relogin(p)) { request.Note = "needs re-login"; if (request.Kind == "collect") { request.Status = "refused"; request.FinishedAt = Clock.Now; } continue; }
            if (await BrowserBusy(p)) { request.Note = "platform busy"; continue; }
            if (request.Kind == "collect" && (await db.Runs.AnyAsync(r => r.Platform == p && r.Kind == "collect" && r.Status == "running", ct) || states.GetValueOrDefault(p)?.LastRunFinishedAt > Clock.Now.AddMinutes(-c.Scheduler.ManualCooldownMinutes))) { request.Note = "waiting for collect/cooldown"; continue; }
            await Dispatch(request, request.RetryIncomplete ? "recovery" : "manual", ct);
        }
        await db.SaveChangesAsync(ct);
        var local = TimeZoneInfo.ConvertTimeFromUtc(Clock.Now, c.Zone); var day = local.ToString("yyyy-MM-dd");
        foreach (var p in Platforms.All.Where(c.Enabled)) foreach (var (modeKey, times) in c.Platform(p).Schedule)
        {
            var mode = Platforms.Mode(modeKey); if (!c.Platform(p).ScheduledOn(mode, DateOnly.FromDateTime(local))) continue;
            foreach (var slot in times)
            {
                var fire = local.Date + TimeSpan.Parse(slot) + TimeSpan.FromMinutes(Jitter(p, mode, slot, day, c.Scheduler.JitterMinutes)); var key = $"slot:{p}:{mode}:{slot}";
                if (local < fire || local > fire.AddMinutes(30) || await db.Get(key, ct) == day || Relogin(p) || await BrowserBusy(p) || await db.Runs.AnyAsync(r => r.Platform == p && r.Kind == "collect" && r.Status == "running", ct) || await db.RunRequests.AnyAsync(r => r.Platform == p && r.Kind == "collect" && (r.Status == "pending" || r.Status == "claimed"), ct)) continue;
                await using (var tx = await db.Database.BeginTransactionAsync(ct)) { await Actions.EnsureRequest(db, "collect", p, mode, ct); await db.Put(key, day, ct); await tx.CommitAsync(ct); }
                var req = await db.RunRequests.AsNoTracking().SingleAsync(r => r.Kind == "collect" && r.Platform == p && r.Status == "pending", ct); await Dispatch(req, "schedule", ct);
            }
        }
        // Only recover targets from an existing cycle. This never starts a new sweep.
        foreach (var p in Platforms.All.Where(c.Enabled))
        {
            var cycle = await db.Get($"sweep:{p}:cycle", ct);
            if (DateTime.TryParse(await db.Get($"coverage:{p}:not_before", ct), out var coverageDelay) && coverageDelay.ToUniversalTime() > Clock.Now) continue;
            if (cycle is null || Relogin(p) || await BrowserBusy(p) || states.GetValueOrDefault(p)?.LastRunFinishedAt > Clock.Now.AddMinutes(-c.Scheduler.ManualCooldownMinutes)
                || await db.Runs.AnyAsync(r => r.Platform == p && r.Kind == "collect" && r.Status == "running", ct)
                || await db.RunRequests.AnyAsync(r => r.Platform == p && r.Kind == "collect" && (r.Status == "pending" || r.Status == "claimed"), ct)) continue;
            if (!await db.SweepTargets.AnyAsync(t => t.Platform == p && t.CycleId == cycle && t.State == "retry" && t.Attempts < TimelineCoverage.MaxAttempts && t.RetryAt <= Clock.Now, ct)) continue;
            if (await db.SweepTargets.AnyAsync(t => t.Platform == p && t.CycleId == cycle && (t.State == "pending" || t.State == "visiting"), ct)) continue;
            if (await Actions.EnsureRequest(db, "collect", p, "all_followed", ct, retryIncomplete: true) == 0) continue;
            var request = await db.RunRequests.SingleAsync(r => r.Platform == p && r.Kind == "collect" && r.Status == "pending", ct);
            // A failed executable/startup must not turn a due visit into a launch storm.
            await db.Put($"coverage:{p}:not_before", Clock.Now.AddHours(1).ToString("O"), ct);
            await Dispatch(request, "recovery", ct);
        }
        var enabled = Platforms.All.Where(c.Enabled).ToArray(); var retry = Clock.Now.AddMinutes(-30); var notBefore = await db.Get("process:not_before", ct);
        bool launchDue = !DateTime.TryParse(notBefore, out var until) || until.ToUniversalTime() <= Clock.Now;
        var modelHash = Prompts.ConfigurationHash(snapshot); var judgeDelay = await db.Get($"model:{modelHash}:judge:not_before", ct); var summaryDelay = await db.Get($"model:{modelHash}:summary:not_before", ct);
        bool judgeDue = !DateTime.TryParse(judgeDelay, out var jd) || jd.ToUniversalTime() <= Clock.Now; bool summaryDue = !DateTime.TryParse(summaryDelay, out var sd) || sd.ToUniversalTime() <= Clock.Now;
        var ingestAvailable = enabled.Where(p => !ResourceLock.Busy(paths, "ingest-" + p)).ToArray();
        var pendingJudgments = db.Posts.Where(p => enabled.Contains(p.Platform) && p.IngestReadyAt != null && (!p.Hidden || p.HiddenBy == "llm") && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision || p.LlmTokenLimit != null));
        bool judgmentReady = c.Llm.Enabled && judgeDue && await pendingJudgments.AnyAsync(JudgmentRetry.Due(c.Llm, Clock.Now), ct);
        bool due = enabled.Length > 0 && (startup && ingestAvailable.Length > 0 || await db.RawSnapshots.AnyAsync(r => ingestAvailable.Contains(r.Platform) && !r.Parsed && !r.Deleted && r.BlockedParserVersion != PayloadParser.Version && !db.Runs.Any(run => run.Id == r.RunId && run.Status == "running" && run.Phase == "browser") && (r.AttemptedAt == null || r.AttemptedAt <= retry), ct) || judgmentReady || c.Llm.Enabled && await db.Posts.AnyAsync(p => enabled.Contains(p.Platform) && p.IngestReadyAt != null && (summaryDue && !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision) && (p.SummaryAttemptedAt == null || p.SummaryAttemptedAt <= retry)), ct));
        if (!due && enabled.Length > 0) due = await db.Media.Join(db.Posts.Where(p => enabled.Contains(p.Platform)), m => m.PostId, p => p.Id, (m, p) => new { m, p }).AnyAsync(x => x.m.Kind == "video" && x.m.IsCurrent && x.m.Path == null && x.m.PrunedAt == null && (x.m.AttemptedAt == null || x.m.AttemptedAt <= retry) && (x.p.Hidden || !c.Llm.Enabled || x.p.CategoriesJson != null && x.p.VerdictContentRevision == x.p.ContentRevision), ct);
        if (ingestAvailable.Length > 0) startup = false;
        if (due && launchDue && !ResourceLock.Busy(paths, "processing")) { await Actions.EnsureRequest(db, "process", null, ct: ct); var request = await db.RunRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Kind == "process" && r.Status == "pending", ct); if (request is not null) await Dispatch(request, "schedule", ct); }
        var utcDay = Clock.Now.ToString("yyyy-MM-dd"); if (await db.Get("maintenance:last", ct) != utcDay)
        {
            await db.Put("maintenance:last", utcDay, ct); using var http = new HttpClient(); var maintenance = new Maintenance(paths, factory, http);
            try { log.LogInformation("{Result}", await maintenance.Prune(c, true, false, null, ct)); log.LogInformation("{Result}", await maintenance.Prune(c, false, false, null, ct)); } catch (ResourceBusyException e) { log.LogInformation("Maintenance deferred: {Reason}", e.Message); }
        }
    }
    async Task Dispatch(RunRequest request, string trigger, CancellationToken ct)
    {
        await using var db = factory.Open(); var token = Guid.NewGuid().ToString("N");
        if (await db.RunRequests.Where(r => r.Id == request.Id && r.Status == "pending").ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "claimed").SetProperty(r => r.ClaimedAt, Clock.Now).SetProperty(r => r.ClaimToken, token).SetProperty(r => r.ChildPid, (int?)null).SetProperty(r => r.ChildStartedAt, (DateTime?)null).SetProperty(r => r.ExitCode, (int?)null), ct) != 1) return;
        try
        {
            Cli = (cliResolver ?? FindCli)(); if (!File.Exists(Cli)) throw new FileNotFoundException("CLI executable does not exist", Cli); var isDll = Cli.EndsWith(".dll", StringComparison.OrdinalIgnoreCase); var start = new ProcessStartInfo(isDll ? "dotnet" : Cli) { UseShellExecute = false, WorkingDirectory = Environment.CurrentDirectory }; if (isDll) start.ArgumentList.Add(Cli);
            foreach (var arg in new[] { request.Kind, "--platform", request.Platform ?? "all", "--request-id", request.Id.ToString(), "--claim-token", token, "--trigger", trigger }) start.ArgumentList.Add(arg);
            if (request.Mode is not null) { start.ArgumentList.Add("--mode"); start.ArgumentList.Add(request.Mode); }
            if (request.RetryIncomplete) start.ArgumentList.Add("--retry-incomplete");
            start.Environment["FEED_DATA"] = paths.Root; var child = Process.Start(start) ?? throw new IOException("CLI failed to start"); log.LogInformation("Dispatched request #{Request} child pid={Pid}", request.Id, child.Id);
            _ = ObserveStartup(child, request.Id, token);
        }
        catch (Exception e)
        {
            await db.RunRequests.Where(r => r.Id == request.Id && r.ClaimToken == token && r.ChildPid == null).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, "refused").SetProperty(r => r.FinishedAt, Clock.Now).SetProperty(r => r.Note, "startup failed: " + e.Message).SetProperty(r => r.ExitCode, 1), ct);
            if (request.Kind == "process") await db.Put("process:not_before", Clock.Now.AddMinutes(30).ToString("O"), ct);
            if (request.Kind == "like") await db.Likes.Where(l => l.Platform == request.Platform && l.State == "pending" && l.AttemptedAt == null).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, "startup failed: " + e.Message), ct);
            throw;
        }
    }
    async Task ObserveStartup(Process child, long id, string token)
    {
        using (child)
        try
        {
            await child.WaitForExitAsync();
            await using var db = factory.Open();
            await using var tx = await db.Database.BeginTransactionAsync();
            var request = await db.RunRequests.SingleOrDefaultAsync(r => r.Id == id && r.Status == "claimed" && r.ClaimToken == token && r.ChildPid == null);
            if (request is null) return; // A registered worker owns its durable outcome.
            var reason = $"startup failed: CLI exited {child.ExitCode} before registering its claim";
            request.Status = "refused"; request.FinishedAt = Clock.Now; request.ExitCode = child.ExitCode == 0 ? 1 : child.ExitCode; request.Note = reason;
            if (request.Kind == "process") await db.Put("process:not_before", Clock.Now.AddMinutes(30).ToString("O"));
            if (request.Kind == "like") await db.Likes.Where(l => l.Platform == request.Platform && l.State == "pending" && l.AttemptedAt == null).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, reason));
            await db.SaveChangesAsync(); await tx.CommitAsync(); log.LogError("Request #{Request}: {Reason}", id, reason);
        }
        catch (Exception e) { log.LogError(e, "Could not observe startup for request #{Request}; durable recovery will reconcile it", id); }
    }
    public static string FindCli()
    {
        if (Environment.GetEnvironmentVariable("FEED_CLI") is { Length: > 0 } configured) return Path.GetFullPath(configured);
        var beside = Path.Combine(AppContext.BaseDirectory, "Feed.Cli.dll"); if (File.Exists(beside)) return beside;
        var dir = new DirectoryInfo(AppContext.BaseDirectory); for (int i = 0; dir is not null && i < 7; i++, dir = dir.Parent) foreach (var config in new[] { "Debug", "Release" }) { var file = Path.Combine(dir.FullName, "src", "Feed.Cli", "bin", config, "net10.0", "Feed.Cli.dll"); if (File.Exists(file)) return file; }
        throw new FileNotFoundException("Feed.Cli not found; publish both apps or set FEED_CLI");
    }
}
