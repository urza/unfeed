using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;

public enum RecoveryDisposition { Attention, Automatic, Paused, Compatibility, Informational }
public sealed record RecoveryIssue(string Platform, string Item, string Problem, int Attempts, DateTime? LastAttempt, DateTime? RetryAfter, string Recovery, bool Informational = false)
{
    public RecoveryDisposition Disposition { get; init; } = RecoveryDisposition.Attention;
    public string? Fingerprint { get; init; }
    public long? RawId { get; init; }
    public int CaptureCount { get; init; } = 1;
    public int RunCount { get; init; } = 1;
    public DateTime? FirstSeen { get; init; }
    public DateTime? LastSeen { get; init; }
    public string? Code { get; init; }
    public long? PostId { get; init; }
    public long? RunId { get; init; }
    public int? NextTokenBudget { get; init; }
}
public static class RecoveryQuery
{
    // The Manage dot is derived from the same rows and dispositions the Manage page shows, so a red dot
    // always has an "attention" row behind it. Cheaper existence queries were tried first and disagreed
    // with the page: they counted parsed captures with auxiliary upstream warnings, which the page files
    // as informational, so the dot was red while Manage reported nothing to do.
    public static async Task<DiagnosticLevel> Level(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
        => Level(await Read(db, s, platform, ct, grouped: true, summaryOnly: true));
    public static DiagnosticLevel Level(IEnumerable<RecoveryIssue> issues)
    {
        var level = DiagnosticLevel.None;
        foreach (var issue in issues)
        {
            if (issue.Disposition == RecoveryDisposition.Attention) return DiagnosticLevel.Alert;
            if (issue.Disposition != RecoveryDisposition.Informational) level = DiagnosticLevel.Notice;
        }
        return level;
    }
    public static async Task<bool> HasIssues(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
        => await Level(db, s, platform, ct) != DiagnosticLevel.None;

    // Raw capture issues age out after a week, parse errors included. An unparsed snapshot that no later
    // capture supersedes (an upstream GraphQL error, for example) would otherwise stay an "attention" row
    // forever and keep the Manage dot lit with nothing left to do. The raw file itself is kept by retention.
    public const int RawIssueDays = 7;
    sealed record RawGroup(string Fingerprint, string Code, string Impact, long Id, int Count, int Runs, DateTime First, DateTime Last);
    public static async Task<RecoveryIssue[]> Read(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default, bool grouped = false, string? fingerprint = null, int page = 1, bool summaryOnly = false)
    {
        var issues = new List<RecoveryIssue>(); var hash = Prompts.ConfigurationHash(s);
        var values = await db.Kv.AsNoTracking().Where(k => k.Key == "process:not_before" || k.Key.StartsWith("model:" + hash)).ToDictionaryAsync(k => k.Key, k => k.Value, ct);
        DateTime? Delay(string key) => DateTime.TryParse(values.GetValueOrDefault(key), out var at) ? at.ToUniversalTime() : null;
        string State(string p, DateTime? at, bool model = false) => !s.Config.Scheduler.Enabled ? "Automatic recovery paused: scheduler disabled" : !s.Config.Enabled(p) ? "Automatic recovery paused: platform disabled" : model && !s.Config.Llm.Enabled ? "Model processing disabled" : at > Clock.Now ? "Automatic retry after delay" : "Due for automatic recovery";
        RecoveryDisposition Disposition(string p, bool model = false) => !s.Config.Scheduler.Enabled || !s.Config.Enabled(p) || model && !s.Config.Llm.Enabled ? RecoveryDisposition.Paused : RecoveryDisposition.Automatic;
        // The two branches spell out PendingWork.Judgment and PendingWork.Summary; an OR of two expressions cannot reuse them in one query. Keep them in step.
        var posts = await db.Posts.AsNoTracking().Where(p => fingerprint == null && p.IngestReadyAt != null && (platform == null || p.Platform == platform)
            && (p.LlmAttemptedAt != null && (!p.Hidden || p.HiddenBy == "llm") && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision || p.LlmTokenLimit != null)
                || p.SummaryAttemptedAt != null && !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision))).ToArrayAsync(ct);
        var refs = posts.Select(p => p.LatestRawRef ?? p.RawRef).Where(r => r != null).ToArray();
        var origins = await db.RawSnapshots.AsNoTracking().Where(r => refs.Contains(r.Path)).Select(r => new { r.Path, r.RunId }).ToArrayAsync(ct);
        foreach (var p in posts) foreach (var task in new[] { "judge", "summary" })
        {
            bool judge = task == "judge"; var error = judge ? p.LlmError : p.SummaryError; var at = judge ? p.LlmAttemptedAt : p.SummaryAttemptedAt;
            bool pending = judge ? (!p.Hidden || p.HiddenBy == "llm") && (!p.Judged || p.LlmTokenLimit != null) : !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision);
            if (!pending || at is null) continue;
            if (error is null && at > Clock.Now.AddSeconds(-s.Config.Llm.TimeoutSeconds * (s.Config.Llm.FallbackBaseUrl.Length > 0 ? 2 : 1))) continue;
            var budget = judge ? JudgmentRetry.Budget(p, s.Config.Llm) : s.Config.Llm.SummaryMaxTokens;
            var retry = budget is null ? null : new[] { judge ? JudgmentRetry.After(p, s.Config.Llm) : at?.AddMinutes(30), Delay($"model:{hash}:{task}:not_before"), Delay("process:not_before") }.Max();
            issues.Add(new(p.Platform, $"post #{p.Id} · {task}", error ?? "Attempt has no current result; worker may still be running, interrupted, or from an older build", judge ? p.LlmFailures : p.SummaryFailures, at, retry, budget is null ? "Automatic token retries exhausted or disabled; raise llm.token_retry_budgets/max_tokens or explicitly rescore" : State(p.Platform, retry, true) + (judge && p.LlmTokenLimit != null ? $"; next max_tokens={budget}" : "")) { Disposition = budget is null ? RecoveryDisposition.Attention : Disposition(p.Platform, true), PostId = p.Id, RunId = origins.FirstOrDefault(r => r.Path == (p.LatestRawRef ?? p.RawRef))?.RunId, NextTokenBudget = judge && p.LlmTokenLimit != null ? budget : null });
        }
        // Positive evidence of capture health. A blocked raw has its own row above; this row covers the case
        // where a changed feed passes nothing through the capture gate, so there is no raw to show.
        if (fingerprint is null) foreach (var p in Platforms.All.Where(p => (platform == null || p == platform) && s.Config.Enabled(p)))
            if (await CaptureHealth.SilentHomeCollects(db, p, ct) is { Length: >= CaptureHealth.SilentCollects } silent)
                issues.Add(new(p, "home collects", $"The last {silent.Length} home collects found no posts", silent.Length, null, null, "The feed shape or the capture gate may have changed. Inspect the raw diagnostics of the last collect runs and the browser session through noVNC.") { RunId = silent[0] });
        // A tool the processing child could not start (plan 10). An absent key means no scheduled run yet, which is
        // not an alarm. yt-dlp missing is an attention row only while video context needs it; the download fallback
        // waits for the tool and is a notice. ffmpeg missing is a notice while vision is on: vision still sends the
        // original image.
        if (fingerprint is null && platform is null) foreach (var tool in ToolHealth.Tools)
            if (await ToolHealth.Read(db, tool, ct) is { Status: not "ok" } state)
            {
                bool attention = tool == "yt-dlp" && s.Config.VideoContext.Enabled, notice = tool == "yt-dlp" || s.Config.Llm.Vision;
                if (!attention && !notice) continue;
                var problem = $"{tool} {(state.Status == "missing" ? "is not installed or not on PATH" : "did not answer a version call: " + state.Error)}";
                var recovery = (tool == "yt-dlp" ? "Install yt-dlp (deployment 16.2) or set video_context.enabled to false. Pending video fallbacks wait for the tool. " : "Install ffmpeg (deployment 16.2). Until then vision sends the original image. ")
                    + "The next processing run clears this row; press Process pending work on the AI processing page to check now.";
                issues.Add(new("tools", tool, problem, 1, state.At, null, recovery, !attention) { Disposition = attention ? RecoveryDisposition.Attention : RecoveryDisposition.Informational });
            }
        var superseded = CaptureHealth.Superseded(db);
        var eligible = db.RawSnapshots.AsNoTracking().Where(r => !superseded.Any(old => old.Id == r.Id) && !r.Deleted && (platform == null || r.Platform == platform) && (!r.Parsed && r.Error != null || r.Parsed && r.Warning != null) && r.CapturedAt > Clock.Now.AddDays(-RawIssueDays));
        var groupedRows = await db.RawDiagnostics.Join(eligible, d => d.RawSnapshotId, r => r.Id, (d, r) => new { d, r })
            .GroupBy(x => new { x.r.Platform, x.d.Fingerprint, x.d.Impact, x.d.Code, x.r.Parsed, x.r.BlockedParserVersion })
            .Select(g => new RawGroup(g.Key.Fingerprint, g.Key.Code, g.Key.Impact, g.Max(x => x.r.Id), g.Select(x => x.r.Id).Distinct().Count(), g.Select(x => x.r.RunId).Distinct().Count(), g.Min(x => x.r.CapturedAt), g.Max(x => x.r.CapturedAt))).ToArrayAsync(ct);
        var legacy = await eligible.Where(r => !db.RawDiagnostics.Any(d => d.RawSnapshotId == r.Id))
            .Select(r => new RawGroup("legacy-" + r.Id, "legacy", r.Parsed ? "auxiliary" : "unknown_or_content", r.Id, 1, 1, r.CapturedAt, r.CapturedAt)).ToArrayAsync(ct);
        groupedRows = groupedRows.Concat(legacy).OrderByDescending(g => g.Last).ThenBy(g => g.Fingerprint, StringComparer.Ordinal).ToArray();
        if (grouped && !summaryOnly) groupedRows = groupedRows.Skip((Math.Max(1, page) - 1) * 50).Take(50).ToArray();
        var ids = groupedRows.Select(g => g.Id).Distinct().ToArray();
        if (fingerprint is not null) eligible = eligible.Where(r => fingerprint == "legacy-" + r.Id || db.RawDiagnostics.Any(d => d.RawSnapshotId == r.Id && d.Fingerprint == fingerprint));
        else if (grouped) eligible = eligible.Where(r => ids.Contains(r.Id));
        var selection = eligible.OrderByDescending(r => r.Id).AsQueryable();
        if (fingerprint is not null) selection = selection.Skip((Math.Max(1, page) - 1) * 50).Take(50);
        var raws = summaryOnly ? await selection.Select(r => new RawSnapshot { Id = r.Id, Platform = r.Platform, Parsed = r.Parsed, RunId = r.RunId,
            AttemptedAt = r.AttemptedAt, BlockedParserVersion = r.BlockedParserVersion }).ToArrayAsync(ct) : await selection.ToArrayAsync(ct);
        foreach (var r in raws)
        {
            var retry = new[] { r.AttemptedAt?.AddMinutes(30), Delay("process:not_before") }.Max();
            var rawIssues = grouped && fingerprint is null ? groupedRows.Where(g => g.Id == r.Id).ToArray() : [];
            foreach (var group in rawIssues.Cast<RawGroup?>().DefaultIfEmpty(null))
            {
            var count = group?.Count ?? 1;
            var informational = group is not null ? group.Impact is "auxiliary" or "partial" : r.Parsed;
            var recovery = r.Parsed ? "Recorded upstream warning; no replay needed" : r.BlockedParserVersion == PayloadParser.Version ? "Needs parser update; saved raw will retry after parser upgrade (explicit reparse also available)" : State(r.Platform, retry);
            if (!informational && group?.Impact == "content") recovery = "Identifiable post content may be missing. " + recovery;
            else if (!informational && group?.Impact == "unknown_or_content") recovery = "Unrecognized response; content impact unresolved. " + recovery;
            issues.Add(new(r.Platform, $"raw #{r.Id} · run #{r.RunId}", summaryOnly ? group?.Code ?? "capture_issue" : r.Error ?? r.Warning ?? "Waiting for ingest", 0, r.AttemptedAt, r.Parsed || r.BlockedParserVersion == PayloadParser.Version ? null : retry,
                informational ? "Recorded upstream warning; no replay needed for this issue" : recovery, informational) { Disposition = informational ? RecoveryDisposition.Informational : r.BlockedParserVersion == PayloadParser.Version ? RecoveryDisposition.Compatibility : Disposition(r.Platform), RunId = r.RunId,
                RawId = r.Id, CaptureCount = count, Fingerprint = group?.Fingerprint ?? fingerprint, Code = group?.Code,
                RunCount = group?.Runs ?? 1, FirstSeen = group?.First, LastSeen = group?.Last });
            }
        }
        return issues.ToArray();
    }
}
