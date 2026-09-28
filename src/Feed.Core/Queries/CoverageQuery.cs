using Feed.Core.Domain;
using Feed.Core.Application;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;
public sealed record CoverageRow(string Platform, string Name, string? Url, DateTime? LastAttempt, DateTime? LastCapture, string Status, int Failures, int PostsFound, DateTime? OldestPost, DateTime? NewestPost, string? Note, string? Screenshot, bool Stale, bool Incomplete, string Recovery);
public static class CoverageQuery
{
    public static async Task<CoverageRow[]> Read(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
    {
        var authors = await db.Authors.AsNoTracking().Where(a => a.IsFriend).ToArrayAsync(ct);
        var visits = await db.TimelineVisits.AsNoTracking().OrderByDescending(v => v.At).ThenByDescending(v => v.Id).ToArrayAsync(ct);
        var byAuthor = visits.Where(v => v.AuthorId != null).ToLookup(v => v.AuthorId!.Value);
        var byUrl = visits.Where(v => v.AuthorId == null).ToLookup(v => (v.Platform, v.Url.TrimEnd('/')));
        var targets = await db.SweepTargets.AsNoTracking().Where(t => db.Kv.Any(k => k.Key == "sweep:" + t.Platform + ":cycle" && k.Value == t.CycleId)).ToArrayAsync(ct);
        var cutoff = Clock.Now.AddHours(-s.Config.Ui.CoverageStaleHours);
        return authors.Where(a => platform is null ? s.Config.Enabled(a.Platform) : a.Platform == platform).Select(a =>
        {
            var history = byAuthor[a.Id].Concat(a.Url is null ? [] : byUrl[(a.Platform, a.Url.TrimEnd('/'))]).OrderByDescending(v => v.At).ThenByDescending(v => v.Id).ToArray();
            var last = history.FirstOrDefault(); var success = history.FirstOrDefault(v => v.CaptureStatus is "captured" or "empty");
            var target = targets.FirstOrDefault(t => t.AuthorId == a.Id);
            var failures = history.TakeWhile(v => v.CaptureStatus == "incomplete" && v.At >= Clock.Now.AddDays(-60)).Count();
            return new CoverageRow(a.Platform, a.DisplayName ?? "Unknown author", a.Url, last?.At, success?.At,
                last is null ? "not checked" : last.CaptureStatus == "empty" ? "explicit empty response" : last.CaptureStatus == "captured" ? "captured · " + last.StopReason : last.Status == "rendered" ? last.PostsFound > 0 ? "partial capture with errors" : "rendered but no matching post payload" : last.Status,
                failures, last?.PostsFound ?? 0, success?.OldestPostedAt, success?.NewestPostedAt, last?.Note, last?.Screenshot, success is null || success.At < cutoff, last is not null && last.CaptureStatus == "incomplete",
                target?.State == "retry" ? $"{target.Attempts}/{TimelineCoverage.MaxAttempts} attempts; retry after {target.RetryAt:u}" + (!s.Config.Scheduler.Enabled ? "; scheduler paused" : !s.Config.Enabled(a.Platform) ? "; platform paused" : "")
                : target?.Attempts >= TimelineCoverage.MaxAttempts && last?.CaptureStatus == "incomplete" ? "Retry limit reached; inspect diagnostics or wait for next scheduled sweep"
                : last?.CaptureStatus == "incomplete" ? "Next configured timeline visit; historical failure retained" : "No recovery pending");
        }).OrderBy(r => r.Platform).ThenBy(r => r.Name).ToArray();
    }
}
