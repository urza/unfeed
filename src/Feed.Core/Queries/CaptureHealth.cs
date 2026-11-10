using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;

public static class CaptureHealth
{
    // Three silent home collects in a row point at a feed shape or a capture gate the parser no longer
    // recognizes. One collect with posts ends the alert: health is judged on positive evidence only.
    public const int SilentCollects = 3;
    // Returns the ids of the silent streak, newest first. A person collect also runs with Mode "home" (the
    // Run row stores no person), so StatsJson.scope is the only mark that keeps it out of the count.
    public static async Task<long[]> SilentHomeCollects(FeedDb db, string platform, CancellationToken ct = default)
    {
        var recent = await db.Runs.AsNoTracking().Where(r => r.Platform == platform && r.Kind == "collect" && r.Mode == "home" && (r.Status == "ok" || r.Status == "capped")
                && (r.StatsJson == null || !r.StatsJson.Contains("\"scope\":\"person\"")))
            .OrderByDescending(r => r.Id).Take(SilentCollects).Select(r => new { r.Id, r.PostsFound }).ToArrayAsync(ct);
        return recent.TakeWhile(r => r.PostsFound == 0).Select(r => r.Id).ToArray();
    }
    // A subsequent successful visit ends the current alert, not the historical gap.
    // No association is inferred from a multi-person run or a display name.
    public static IQueryable<TimelineVisit> CleanVisits(FeedDb db) => db.TimelineVisits.Where(next =>
        next.Status == "rendered" && next.CaptureStatus == "captured" && next.PostsFound > 0 && next.CaptureToken != null &&
        db.Runs.Any(run => run.Id == next.RunId && run.FinishedAt != null && run.Status != "running") &&
        db.RawSnapshots.Any(r => r.CaptureToken == next.CaptureToken && r.Platform == next.Platform && r.Parsed) &&
        !db.RawSnapshots.Any(r => r.CaptureToken == next.CaptureToken && r.Platform == next.Platform && !r.Parsed));
    public static IQueryable<RawSnapshot> Superseded(FeedDb db)
    {
        var clean = CleanVisits(db);
        return db.RawSnapshots.Where(raw => raw.CaptureToken != null &&
            db.TimelineVisits.Any(old => old.CaptureToken == raw.CaptureToken && old.Platform == raw.Platform &&
                clean.Any(next => next.Platform == old.Platform && next.At > old.At && next.RunId != old.RunId &&
                    (old.AuthorId != null ? next.AuthorId == old.AuthorId : next.AuthorId == null && next.Url == old.Url))));
    }
}
