using System.Globalization;
using Feed.Core.Domain;
using Feed.Core.Application;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Feed.Core.Queries;
public sealed record CoverageRow(string Platform, string Name, string? Url, DateTime? LastAttempt, DateTime? LastCapture, string Status, int Failures, int PostsFound, DateTime? OldestPost, DateTime? NewestPost, string? Note, string? Screenshot, bool Stale, bool Incomplete, string Recovery);
public static class CoverageQuery
{
    static bool IsNewer(DateTime at, long id, DateTime otherAt, long otherId)
        => at > otherAt || at == otherAt && id > otherId;

    readonly record struct IssueSummary(DateTime LatestAt, long LatestId, string LatestStatus, DateTime? LastSuccessAt);

    static IssueSummary ParseIssueSummary(string packedLatest, DateTime? lastSuccess)
    {
        // Packed as "<At>#<zero-padded Id>#<CaptureStatus>" so SQLite MAX picks the exact (At DESC, Id DESC) winner
        // ('#'=35 sorts below '.'=46 so mixed fractional timestamps order chronologically).
        int first = packedLatest.IndexOf('#');
        int second = packedLatest.IndexOf('#', first + 1);
        var at = SqliteDates.Parse(packedLatest[..first]);
        var id = long.Parse(packedLatest.AsSpan(first + 1, second - first - 1), CultureInfo.InvariantCulture);
        var status = packedLatest[(second + 1)..];
        return new(at, id, status, lastSuccess);
    }

    sealed class RowAgg
    {
        public bool HasLast;
        public DateTime LastAt;
        public long LastId;
        public string LastStatus = "", LastCaptureStatus = "", LastStopReason = "", LastNote = "";
        public int LastPostsFound;
        public string? LastScreenshot;

        public bool HasSuccess;
        public DateTime SuccessAt;
        public long SuccessId;
        public DateTime? SuccessOldest, SuccessNewest;

        public bool HasNonIncomplete;
        public DateTime MaxNonIncompleteAt;
        public long MaxNonIncompleteId;
        public List<(DateTime At, long Id)>? RecentIncomplete;
    }

    public static async Task<bool> HasIssues(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
    {
        var scoped = platform is null ? Platforms.All.Where(s.Config.Enabled).ToArray() : [platform];
        if (scoped.Length == 0) return false;
        var scopedSet = new HashSet<string>(scoped, StringComparer.Ordinal);

        await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;

        var authors = new List<(long Id, string Platform, string? Url)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id, Platform, Url FROM Authors WHERE IsFriend = 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = r.GetString(1);
                if (scopedSet.Contains(p)) authors.Add((r.GetInt64(0), p, r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }
        if (authors.Count == 0) return false;

        // Aggregate per author and per legacy URL-only key in one sequential SQLite pass (+Platform avoids random
        // lookups through IX_TimelineVisits_Platform_Url_Id), transferring only one summary row per author/URL.
        var byAuthor = new Dictionary<long, IssueSummary>(authors.Count);
        var byUrl = new Dictionary<(string Platform, string Url), IssueSummary>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Platform,
                       AuthorId,
                       CASE WHEN AuthorId IS NULL THEN RTRIM(Url, '/') END,
                       MAX(At || '#' || printf('%019d', Id) || '#' || CaptureStatus),
                       MAX(CASE WHEN CaptureStatus IN ('captured','empty') THEN At END)
                FROM TimelineVisits
                GROUP BY +Platform, AuthorId, CASE WHEN AuthorId IS NULL THEN RTRIM(Url, '/') END
                """;
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = r.GetString(0);
                if (!scopedSet.Contains(p)) continue;
                var summary = ParseIssueSummary(r.GetString(3), r.IsDBNull(4) ? null : SqliteDates.Parse(r.GetString(4)));
                if (!r.IsDBNull(1)) byAuthor[r.GetInt64(1)] = summary;
                else byUrl[(p, r.GetString(2))] = summary;
            }
        }

        var cutoffs = scoped.ToDictionary(p => p, p => StaleCutoff(s, p), StringComparer.Ordinal);
        foreach (var a in authors)
        {
            bool hasA = byAuthor.TryGetValue(a.Id, out var av);
            IssueSummary uv = default;
            bool hasU = a.Url is not null && byUrl.TryGetValue((a.Platform, a.Url.TrimEnd('/')), out uv);
            DateTime? successAt = hasA && av.LastSuccessAt is { } aSuc && hasU && uv.LastSuccessAt is { } uSuc
                ? (aSuc > uSuc ? aSuc : uSuc)
                : (hasA ? av.LastSuccessAt : null) ?? (hasU ? uv.LastSuccessAt : null);
            if (successAt is null || successAt < cutoffs[a.Platform]) return true;
            IssueSummary? latest = hasA && hasU
                ? (IsNewer(av.LatestAt, av.LatestId, uv.LatestAt, uv.LatestId) || (av.LatestAt == uv.LatestAt && av.LatestId == uv.LatestId) ? av : uv)
                : hasA ? av : hasU ? uv : null;
            if (latest?.LatestStatus == "incomplete") return true;
        }
        return false;
    }

    // A timeline is only visited by the all_followed sweep, so "not checked recently" must be measured
    // against that sweep's own cadence: with a 6 or 11 day interval a fixed 48 hour threshold flagged
    // every author for most of each cycle and the Manage dot never went out. Half an interval of slack
    // absorbs jitter, skipped days and a sweep that ran while the host was down. ui.coverage_stale_hours
    // still applies when it is the larger value or when no interval is configured.
    public static DateTime StaleCutoff(InstanceSnapshot s, string platform)
    {
        var hours = s.Config.Ui.CoverageStaleHours;
        if (s.Config.Platforms.TryGetValue(platform, out var p) && p.ScheduleIntervals.TryGetValue("all_followed", out var interval)) hours = Math.Max(hours, interval.Days * 36);
        return Clock.Now.AddHours(-hours);
    }

    public static async Task<CoverageRow[]> Read(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default)
    {
        var scoped = platform is null ? Platforms.All.Where(s.Config.Enabled).ToArray() : [platform];
        if (scoped.Length == 0) return [];
        var scopedSet = new HashSet<string>(scoped, StringComparer.Ordinal);

        await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;

        var authors = new List<(long Id, string Platform, string? DisplayName, string? Url)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id, Platform, DisplayName, Url FROM Authors WHERE IsFriend = 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = r.GetString(1);
                if (scopedSet.Contains(p)) authors.Add((r.GetInt64(0), p, r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }
        if (authors.Count == 0) return [];

        var failureCutoff = Clock.Now.AddDays(-60);
        var byAuthor = new Dictionary<long, RowAgg>(authors.Count);
        var byUrl = new Dictionary<(string Platform, string Url), RowAgg>();

        // Reverse PK scan (ORDER BY Id DESC is a zero-sort reverse B-tree scan on rowid): visits are recorded
        // newest-last, so encountering newest rows first means older rows skip reading Status/StopReason/Note/
        // Screenshot and parsing OldestPostedAt/NewestPostedAt while still staying exact for out-of-order timestamps.
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id, Platform, AuthorId, Url, At, Status, CaptureStatus, PostsFound, StopReason, OldestPostedAt, NewestPostedAt, Note, Screenshot FROM TimelineVisits ORDER BY Id DESC";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var p = r.GetString(1);
                if (!scopedSet.Contains(p)) continue;
                RowAgg agg;
                if (!r.IsDBNull(2))
                {
                    long aid = r.GetInt64(2);
                    if (!byAuthor.TryGetValue(aid, out agg!)) byAuthor[aid] = agg = new();
                }
                else
                {
                    var key = (p, r.GetString(3).TrimEnd('/'));
                    if (!byUrl.TryGetValue(key, out agg!)) byUrl[key] = agg = new();
                }

                long id = r.GetInt64(0);
                var at = SqliteDates.Parse(r.GetString(4));
                var captureStatus = r.GetString(6);

                if (!agg.HasLast || IsNewer(at, id, agg.LastAt, agg.LastId))
                {
                    agg.HasLast = true;
                    agg.LastAt = at;
                    agg.LastId = id;
                    agg.LastStatus = r.GetString(5);
                    agg.LastCaptureStatus = captureStatus;
                    agg.LastPostsFound = r.GetInt32(7);
                    agg.LastStopReason = r.IsDBNull(8) ? "" : r.GetString(8);
                    agg.LastNote = r.IsDBNull(11) ? "" : r.GetString(11);
                    agg.LastScreenshot = r.IsDBNull(12) ? null : r.GetString(12);
                }

                if (captureStatus is "captured" or "empty")
                {
                    if (!agg.HasSuccess || IsNewer(at, id, agg.SuccessAt, agg.SuccessId))
                    {
                        agg.HasSuccess = true;
                        agg.SuccessAt = at;
                        agg.SuccessId = id;
                        agg.SuccessOldest = r.IsDBNull(9) ? null : SqliteDates.Parse(r.GetString(9));
                        agg.SuccessNewest = r.IsDBNull(10) ? null : SqliteDates.Parse(r.GetString(10));
                    }
                }

                if (captureStatus == "incomplete")
                {
                    if (at >= failureCutoff) (agg.RecentIncomplete ??= []).Add((at, id));
                }
                else if (!agg.HasNonIncomplete || IsNewer(at, id, agg.MaxNonIncompleteAt, agg.MaxNonIncompleteId))
                {
                    agg.HasNonIncomplete = true;
                    agg.MaxNonIncompleteAt = at;
                    agg.MaxNonIncompleteId = id;
                }
            }
        }

        var targets = await db.SweepTargets.AsNoTracking().Where(t => scoped.Contains(t.Platform) && db.Kv.Any(k => k.Key == "sweep:" + t.Platform + ":cycle" && k.Value == t.CycleId)).ToArrayAsync(ct);
        var targetByAuthor = new Dictionary<long, SweepTarget>(targets.Length);
        foreach (var t in targets) targetByAuthor.TryAdd(t.AuthorId, t);
        var cutoffs = scoped.ToDictionary(p => p, p => StaleCutoff(s, p), StringComparer.Ordinal);

        return authors.Select(a =>
        {
            byAuthor.TryGetValue(a.Id, out var aa);
            RowAgg? ua = a.Url is not null && byUrl.TryGetValue((a.Platform, a.Url.TrimEnd('/')), out var u) ? u : null;

            RowAgg? last = aa is { HasLast: true } && ua is { HasLast: true }
                ? (IsNewer(aa.LastAt, aa.LastId, ua.LastAt, ua.LastId) ? aa : ua)
                : aa is { HasLast: true } ? aa : ua is { HasLast: true } ? ua : null;
            RowAgg? success = aa is { HasSuccess: true } && ua is { HasSuccess: true }
                ? (IsNewer(aa.SuccessAt, aa.SuccessId, ua.SuccessAt, ua.SuccessId) ? aa : ua)
                : aa is { HasSuccess: true } ? aa : ua is { HasSuccess: true } ? ua : null;

            // Leading consecutive incomplete visits within 60 days are exactly the recent incomplete visits
            // strictly newer in (At, Id) than the newest non-incomplete visit.
            bool hasBarrier = false;
            DateTime barrierAt = default;
            long barrierId = 0;
            if (aa is { HasNonIncomplete: true }) { hasBarrier = true; barrierAt = aa.MaxNonIncompleteAt; barrierId = aa.MaxNonIncompleteId; }
            if (ua is { HasNonIncomplete: true } && (!hasBarrier || IsNewer(ua.MaxNonIncompleteAt, ua.MaxNonIncompleteId, barrierAt, barrierId)))
            {
                hasBarrier = true; barrierAt = ua.MaxNonIncompleteAt; barrierId = ua.MaxNonIncompleteId;
            }
            int failures = 0;
            if (aa?.RecentIncomplete is { } aInc)
                foreach (var (at, id) in aInc) if (!hasBarrier || IsNewer(at, id, barrierAt, barrierId)) failures++;
            if (ua?.RecentIncomplete is { } uInc)
                foreach (var (at, id) in uInc) if (!hasBarrier || IsNewer(at, id, barrierAt, barrierId)) failures++;

            var target = targetByAuthor.GetValueOrDefault(a.Id);
            return new CoverageRow(a.Platform, a.DisplayName ?? "Unknown author", a.Url, last?.LastAt, success?.SuccessAt,
                last is null ? "not checked" : last.LastCaptureStatus == "empty" ? "explicit empty response" : last.LastCaptureStatus == "captured" ? "captured · " + last.LastStopReason : last.LastStatus == "rendered" ? last.LastPostsFound > 0 ? "partial capture with errors" : "rendered but no matching post payload" : last.LastStatus,
                failures, last?.LastPostsFound ?? 0, success?.SuccessOldest, success?.SuccessNewest, last?.LastNote ?? "", last?.LastScreenshot, success is null || success.SuccessAt < cutoffs[a.Platform], last is not null && last.LastCaptureStatus == "incomplete",
                last?.LastStopReason == "parser_blocked" ? "Saved response awaits a parser update; no browser retry for an unsupported shape"
                : target?.State == "retry" ? $"{target.Attempts}/{TimelineCoverage.MaxAttempts} attempts; retry after {target.RetryAt:u}" + (!s.Config.Scheduler.Enabled ? "; scheduler paused" : !s.Config.Enabled(a.Platform) ? "; platform paused" : "")
                : target?.Attempts >= TimelineCoverage.MaxAttempts && last?.LastCaptureStatus == "incomplete" ? "Retry limit reached; inspect diagnostics or wait for next scheduled sweep"
                : last?.LastCaptureStatus == "incomplete" ? "Next configured timeline visit; historical failure retained" : "No recovery pending");
        }).OrderBy(r => r.Platform).ThenBy(r => r.Name).ToArray();
    }
}
