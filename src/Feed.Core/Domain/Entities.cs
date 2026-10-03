namespace Feed.Core.Domain;

public static class Clock { public static DateTime Now { get { var now = DateTime.UtcNow; return now.AddTicks(-(now.Ticks % TimeSpan.TicksPerSecond)); } } }
public sealed class Author
{
    public long Id { get; set; } public string Platform { get; set; } = ""; public string? PlatformAuthorId { get; set; } public string? DisplayName { get; set; } public string? Url { get; set; }
    public bool IsFriend { get; set; } public string? AvatarPath { get; set; } public string RefsJson { get; set; } = "[]";
    public DateTime FirstSeenAt { get; set; } = Clock.Now; public DateTime LastSeenAt { get; set; } = Clock.Now;
}
public sealed class AuthorKey { public string Key { get; set; } = ""; public string Platform { get; set; } = ""; public long AuthorId { get; set; } }
public sealed class Post
{
    public long Id { get; set; } public string Platform { get; set; } = ""; public string PlatformPostId { get; set; } = ""; public long? AuthorId { get; set; }
    public string? ObservedAuthorName { get; set; } public string? ObservedAuthorUrl { get; set; } public DateTime? PostedAt { get; set; } public DateTime CapturedAt { get; set; } = Clock.Now; public DateTime CreatedAt { get; set; } = Clock.Now;
    public string? RawRef { get; set; } public string? LatestRawRef { get; set; } public string MediaManifestJson { get; set; } = "[]";
    public int ContentRevision { get; set; } = 1; public string ContentHash { get; set; } = ""; public DateTime? IngestReadyAt { get; set; }
    public string? Text { get; set; } public string? Permalink { get; set; } public string? LikeRef { get; set; }
    public bool IsSponsored { get; set; } public bool IsSuggested { get; set; } public bool IsReel { get; set; } public bool IsEvent { get; set; }
    public string? MemoryLabel { get; set; } public string? MemoryText { get; set; } public string? TagsJson { get; set; }
    public string? StoryTitle { get; set; }
    public string? SharedAuthor { get; set; } public string? SharedText { get; set; } public string? SharedUrl { get; set; }
    public string? CategoriesJson { get; set; } public int? LlmScore { get; set; } public string? LlmReason { get; set; } public string? PrefsVersion { get; set; }
    public int? VerdictContentRevision { get; set; } public string? VerdictInputHash { get; set; } public string? VerdictModel { get; set; } public string? VerdictEndpoint { get; set; } public DateTime? JudgedAt { get; set; }
    public string? Summary { get; set; } public int? SummaryContentRevision { get; set; } public string? SummaryInputHash { get; set; } public string? SummaryModel { get; set; } public string? SummaryEndpoint { get; set; } public DateTime? SummarizedAt { get; set; }
    public DateTime? LlmAttemptedAt { get; set; } public DateTime? SummaryAttemptedAt { get; set; }
    public string? LlmError { get; set; } public string? SummaryError { get; set; }
    public int? LlmTokenLimit { get; set; }
    public int LlmFailures { get; set; } public int SummaryFailures { get; set; }
    public bool Hidden { get; set; } public string? HiddenBy { get; set; } public string? HiddenReason { get; set; } public DateTime? HiddenAt { get; set; } public int VisibilityRevision { get; set; }
    public void SetHidden(string owner, string reason) { if (!ClosedValues.HideOwners.Contains(owner) || owner == "thumbs") throw new ArgumentException("Invalid hide owner"); if (!Hidden) HiddenAt = Clock.Now; Hidden = true; HiddenBy = owner; HiddenReason = reason; VisibilityRevision++; }
    public void ClearHidden() { Hidden = false; HiddenBy = HiddenReason = null; HiddenAt = null; VisibilityRevision++; }
    public bool Judged => CategoriesJson is not null && VerdictContentRevision == ContentRevision;
    public string DisplayText => string.Join("\n\n", new[] { Text, SharedText }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
public sealed class Media
{
    public long Id { get; set; } public long PostId { get; set; } public string Kind { get; set; } = "image"; public string? OriginalUrl { get; set; } public string? Path { get; set; }
    public int? Width { get; set; } public int? Height { get; set; } public int Position { get; set; } public string SourceKey { get; set; } = ""; public string? ContentHash { get; set; } public bool IsCurrent { get; set; } = true;
    public DateTime? AttemptedAt { get; set; } public int DownloadAttempts { get; set; } public string? Error { get; set; } public DateTime CreatedAt { get; set; } = Clock.Now; public DateTime? PrunedAt { get; set; }
}
public sealed class RawSnapshot
{
    public long Id { get; set; } public string Platform { get; set; } = ""; public long? RunId { get; set; } public string Path { get; set; } = ""; public string Kind { get; set; } = "feed"; public DateTime CapturedAt { get; set; } = Clock.Now;
    public bool Parsed { get; set; } public DateTime? AttemptedAt { get; set; } public DateTime? ParsedAt { get; set; } public string? Error { get; set; } public bool Deleted { get; set; } public long Bytes { get; set; }
    public int? BlockedParserVersion { get; set; } public string? Warning { get; set; }
}
public sealed class Run
{
    public long Id { get; set; } public string? Platform { get; set; } public string Kind { get; set; } = ""; public string? Mode { get; set; } public string Phase { get; set; } = "starting";
    public int ProcessPid { get; set; } public DateTime ProcessStartedAt { get; set; } public long? RequestId { get; set; } public string Trigger { get; set; } = "manual"; public string Status { get; set; } = "running";
    public DateTime StartedAt { get; set; } = Clock.Now; public DateTime? FinishedAt { get; set; } public int PostsFound { get; set; } public int PostsNew { get; set; } public string? StatsJson { get; set; } public string? Error { get; set; } public string? RawDir { get; set; }
}
public sealed class RunRequest
{
    public long? PersonAuthorId { get; set; } public string? Person { get; set; }
    public bool RetryIncomplete { get; set; }
    public long Id { get; set; } public string Kind { get; set; } = "collect"; public string? Platform { get; set; } public string? Mode { get; set; } public string Status { get; set; } = "pending";
    public DateTime RequestedAt { get; set; } = Clock.Now; public DateTime? ClaimedAt { get; set; } public DateTime? FinishedAt { get; set; } public string? ClaimToken { get; set; }
    public int? ChildPid { get; set; } public DateTime? ChildStartedAt { get; set; } public int? ExitCode { get; set; } public string? Note { get; set; }
}
public sealed class Feedback
{
    public long Id { get; set; } public long PostId { get; set; } public int Value { get; set; } public DateTime At { get; set; } = Clock.Now; public string? Platform { get; set; } public long? AuthorId { get; set; }
    public string? ViewKey { get; set; } public string? Scope { get; set; } public string? CategoriesAtVote { get; set; } public int? ScoreAtVote { get; set; } public string? ReasonAtVote { get; set; } public bool Curated { get; set; }
}
public sealed class Like
{
    public long Id { get; set; } public long PostId { get; set; } public string Platform { get; set; } = ""; public string State { get; set; } = "pending"; public string? Error { get; set; }
    public DateTime RequestedAt { get; set; } = Clock.Now; public DateTime? SentAt { get; set; } public DateTime? AttemptedAt { get; set; }
}
public sealed class PlatformState { public string Platform { get; set; } = ""; public bool NeedsRelogin { get; set; } public DateTime? LastOkRunAt { get; set; } public DateTime? LastRunFinishedAt { get; set; } public DateTime UpdatedAt { get; set; } = Clock.Now; }
public sealed class TimelineVisit
{
    public long Id { get; set; } public string Platform { get; set; } = ""; public long? AuthorId { get; set; } public string Url { get; set; } = ""; public string Name { get; set; } = ""; public long RunId { get; set; } public DateTime At { get; set; } = Clock.Now;
    public string Status { get; set; } = "unrendered"; public string CaptureStatus { get; set; } = "incomplete"; public int PostsFound { get; set; } public DateTime? NewestPostedAt { get; set; } public DateTime? OldestPostedAt { get; set; } public string StopReason { get; set; } = "interrupted"; public string? Note { get; set; } public string? Screenshot { get; set; }
}
public sealed class SweepTarget { public string Platform { get; set; } = ""; public string CycleId { get; set; } = ""; public long AuthorId { get; set; } public int Position { get; set; } public string State { get; set; } = "pending"; public long? RunId { get; set; } public long? VisitId { get; set; } public DateTime? AttemptedAt { get; set; } public int Attempts { get; set; } public DateTime? RetryAt { get; set; } }
public sealed class Kv { public string Key { get; set; } = ""; public string Value { get; set; } = ""; }
public static class ClosedValues
{
    public static readonly string[] HideOwners = ["structural", "whitelist", "keyword", "mute", "llm", "thumbs", "other"];
    public static readonly string[] RunStatuses = ["running", "ok", "capped", "checkpoint", "error", "refused", "imported", "cancelled"];
    public static readonly string[] RunKinds = ["collect", "process", "friends", "reparse", "rescore", "summarize", "refilter", "like", "login"];
}
public sealed record Tag(string Name, string? Url, string? Kind = null);
public sealed record MediaSource(string Kind, string Url, string SourceKey);
public sealed record Observation(Post Post, string? AuthorKey, string? AuthorName, string? AuthorUrl, IReadOnlyList<MediaSource> Media)
{
    public IReadOnlyList<string> TimelineOwnerIds { get; init; } = [];
    public bool IsPartial { get; init; }
}
public sealed record Person(string? Id, string? Name, string? Url, string? Avatar = null);
