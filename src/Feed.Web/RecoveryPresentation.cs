using Feed.Core.Domain;
using Feed.Core.Queries;

namespace Feed.Web;

public static class RecoveryPresentation
{
    public static string Category(RecoveryIssue issue) => issue.Informational ? "notice"
        : issue.Problem.Contains("token budget exhausted", StringComparison.OrdinalIgnoreCase) ? "tokens"
        : issue.Item.EndsWith("· judge") ? "classification"
        : issue.Item.EndsWith("· summary") ? "summary" : "capture";
    public static string Title(string category) => category switch {
        "tokens" => "AI reached its response limit", "classification" => "Classification did not finish",
        "summary" => "Summary did not finish", "notice" => "Upstream notices recorded", _ => "Saved capture needs processing"
    };
    public static string Explanation(string category) => category switch {
        "tokens" => "The post is stored, but the model used its response allowance before completing classification.",
        "classification" => "The post is stored. Classification needs another attempt; collecting the post again is not necessary.",
        "summary" => "The post is stored. Its summary needs another attempt.",
        "notice" => "These captures were processed. No replay is needed for these recorded warnings.",
        _ => "Feed kept the captured data. Inspect the recovery instructions before collecting again."
    };
    public static string Label(RecoveryDisposition state) => state switch {
        RecoveryDisposition.Automatic => "Retrying automatically", RecoveryDisposition.Paused => "Recovery paused",
        RecoveryDisposition.Informational => "Informational notices", _ => "Needs your attention"
    };
    public static string Timing(RecoveryIssue issue, DateTime now) => issue.Disposition switch {
        RecoveryDisposition.Automatic when issue.RetryAfter > now => "Retry eligible " + Management.RelativeTime(issue.RetryAfter.Value, now),
        RecoveryDisposition.Automatic => "Eligible for retry; waiting for a processing opportunity. This does not mean an attempt is running.",
        _ => issue.Recovery
    };
    public static string RunStatus(Run run, IEnumerable<TimelineVisit> visits) => run.Status switch {
        "running" => "In progress", "ok" when visits.Any(v => v.CaptureStatus == "incomplete") => "Finished with coverage gaps",
        "ok" or "imported" => "Finished", "capped" => "Stopped at the configured limit", "checkpoint" => "Login needs attention",
        "error" => "Failed", "refused" => "Could not start", "cancelled" => "Cancelled", _ => run.Status
    };
    public static string At(DateTime at, TimeZoneInfo zone) => $"{TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(at, DateTimeKind.Utc), zone):yyyy-MM-dd HH:mm:ss} ({zone.Id})";
}
