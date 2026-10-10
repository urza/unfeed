using Feed.Core.Domain;
using Feed.Core.Infrastructure;
namespace Feed.Core.Queries;

public static class CaptureHealth
{
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
