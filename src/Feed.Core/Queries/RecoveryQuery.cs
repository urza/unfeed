using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;

public sealed record RecoveryIssue(string Platform, string Item, string Problem, int Attempts, DateTime? LastAttempt, DateTime? RetryAfter, string Recovery, bool Informational = false);
public static class RecoveryQuery
{
    public static async Task<RecoveryIssue[]> Read(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
    {
        var issues = new List<RecoveryIssue>(); var hash = Prompts.ConfigurationHash(s);
        var values = await db.Kv.AsNoTracking().Where(k => k.Key == "process:not_before" || k.Key.StartsWith("model:" + hash)).ToDictionaryAsync(k => k.Key, k => k.Value, ct);
        DateTime? Delay(string key) => DateTime.TryParse(values.GetValueOrDefault(key), out var at) ? at.ToUniversalTime() : null;
        string State(string p, DateTime? at, bool model = false) => !s.Config.Scheduler.Enabled ? "Automatic recovery paused: scheduler disabled" : !s.Config.Enabled(p) ? "Automatic recovery paused: platform disabled" : model && !s.Config.Llm.Enabled ? "Model processing disabled" : at > Clock.Now ? "Automatic retry after delay" : "Due for automatic recovery";
        var posts = await db.Posts.AsNoTracking().Where(p => p.IngestReadyAt != null && (platform == null || p.Platform == platform)
            && (p.LlmAttemptedAt != null && (!p.Hidden || p.HiddenBy == "llm") && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision)
                || p.SummaryAttemptedAt != null && !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision))).ToArrayAsync(ct);
        foreach (var p in posts) foreach (var task in new[] { "judge", "summary" })
        {
            bool judge = task == "judge"; var error = judge ? p.LlmError : p.SummaryError; var at = judge ? p.LlmAttemptedAt : p.SummaryAttemptedAt;
            bool pending = judge ? (!p.Hidden || p.HiddenBy == "llm") && !p.Judged : !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision);
            if (!pending || at is null) continue;
            if (error is null && at > Clock.Now.AddSeconds(-s.Config.Llm.TimeoutSeconds * (s.Config.Llm.FallbackBaseUrl.Length > 0 ? 2 : 1))) continue;
            var retry = new[] { at?.AddMinutes(30), Delay($"model:{hash}:{task}:not_before"), Delay("process:not_before") }.Max();
            issues.Add(new(p.Platform, $"post #{p.Id} · {task}", error ?? "Attempt has no current result; worker may still be running, interrupted, or from an older build", judge ? p.LlmFailures : p.SummaryFailures, at, retry, State(p.Platform, retry, true)));
        }
        var raws = await db.RawSnapshots.AsNoTracking().Where(r => !r.Deleted && (platform == null || r.Platform == platform) && (!r.Parsed && r.Error != null || r.Parsed && r.Warning != null && r.CapturedAt > Clock.Now.AddDays(-7))).OrderByDescending(r => r.Id).ToArrayAsync(ct);
        foreach (var r in raws)
        {
            var retry = new[] { r.AttemptedAt?.AddMinutes(30), Delay("process:not_before") }.Max();
            issues.Add(new(r.Platform, $"raw #{r.Id} · run #{r.RunId}", r.Error ?? r.Warning ?? "Waiting for ingest", 0, r.AttemptedAt, r.Parsed || r.BlockedParserVersion == PayloadParser.Version ? null : retry,
                r.Parsed ? "Recorded upstream warning; no replay needed" : r.BlockedParserVersion == PayloadParser.Version ? "Needs parser update; saved raw will retry after parser upgrade (explicit reparse also available)" : State(r.Platform, retry), r.Parsed));
        }
        return issues.ToArray();
    }
}
