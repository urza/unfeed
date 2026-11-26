using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Feed.Core.Queries;

// Compatibility: a parser-blocked response where posts can be missing. Backlog: a parser-blocked response
// beside captured posts; parser work with nothing for the owner to do, so it lights no dot and stays off the overview.
public enum RecoveryDisposition { Attention, Automatic, Paused, Compatibility, Informational, Backlog }
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
            if (issue.Disposition is not (RecoveryDisposition.Informational or RecoveryDisposition.Backlog)) level = DiagnosticLevel.Notice;
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
    sealed record RawCandidate(long Id, string Platform, long? RunId, DateTime CapturedAt, bool Parsed, DateTime? AttemptedAt, string? Error, string? Warning, int? BlockedParserVersion, string? CaptureToken);
    sealed record DiagRow(long RawSnapshotId, string Fingerprint, string Code, string Impact);

    // Reads both the unpaginated summary (for the Manage overview / RecoverySummary header) and the current
    // page of recovery issues in a single database pass instead of querying Posts/RawSnapshots/Visits twice.
    public static async Task<(RecoveryIssue[] Summary, RecoveryIssue[] Page)> ReadWithSummary(FeedDb db, InstanceSnapshot s, string? signature = null, int page = 1, CancellationToken ct = default)
    {
        var ctx = await LoadContext(db, s, null, ct, includePostAndEnv: true, loadRawText: true);
        var summary = BuildIssues(ctx, s, grouped: true, fingerprint: null, page: 1, summaryOnly: true);
        var details = BuildIssues(ctx, s, grouped: signature is null, fingerprint: signature, page: page, summaryOnly: false);
        return (summary, details);
    }

    public static async Task<RecoveryIssue[]> Read(FeedDb db, InstanceSnapshot s, string? platform = null, CancellationToken ct = default, bool grouped = false, string? fingerprint = null, int page = 1, bool summaryOnly = false)
    {
        var ctx = await LoadContext(db, s, platform, ct, includePostAndEnv: fingerprint is null, loadRawText: !summaryOnly);
        return BuildIssues(ctx, s, grouped, fingerprint, page, summaryOnly);
    }

    sealed record RecoveryContext(
        string ConfigHash,
        Dictionary<string, string> KvValues,
        List<RecoveryIssue> BaseIssues,
        RawCandidate[] EligibleRaws,
        DiagRow[] Diagnostics,
        Dictionary<string, int> VisitPosts,
        Dictionary<long, int> RunPosts);

    static async Task<RecoveryContext> LoadContext(FeedDb db, InstanceSnapshot s, string? platform, CancellationToken ct, bool includePostAndEnv, bool loadRawText)
    {
        var hash = Prompts.ConfigurationHash(s);
        var values = await db.Kv.AsNoTracking().Where(k => k.Key == "process:not_before" || k.Key.StartsWith("model:" + hash)).ToDictionaryAsync(k => k.Key, k => k.Value, ct);
        DateTime? Delay(string key) => DateTime.TryParse(values.GetValueOrDefault(key), out var at) ? at.ToUniversalTime() : null;
        string State(string p, DateTime? at, bool model = false) => !s.Config.Scheduler.Enabled ? "Automatic recovery paused: scheduler disabled" : !s.Config.Enabled(p) ? "Automatic recovery paused: platform disabled" : model && !s.Config.Llm.Enabled ? "Model processing disabled" : at > Clock.Now ? "Automatic retry after delay" : "Due for automatic recovery";
        RecoveryDisposition Disposition(string p, bool model = false) => !s.Config.Scheduler.Enabled || !s.Config.Enabled(p) || model && !s.Config.Llm.Enabled ? RecoveryDisposition.Paused : RecoveryDisposition.Automatic;

        var baseIssues = new List<RecoveryIssue>();
        await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var tx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;

        if (includePostAndEnv)
        {
            // Query judge and summary candidates separately using SQL predicates that exactly match the partial
            // indexes IX_Posts_JudgeQueue and IX_Posts_SummaryQueue (Hidden = 0 instead of EF's NOT (Hidden)).
            var postIssues = new List<(long Id, string Platform, string Task, string? Error, int Failures, DateTime? AttemptedAt, int? TokenLimit, string? RawRef)>();
            var rawRefs = new HashSet<string>(StringComparer.Ordinal);
            var staleCutoff = Clock.Now.AddSeconds(-s.Config.Llm.TimeoutSeconds * (s.Config.Llm.FallbackBaseUrl.Length > 0 ? 2 : 1));
            var platforms = platform is null ? Platforms.All : [platform];

            foreach (var plat in platforms)
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT Id, LlmError, LlmFailures, LlmAttemptedAt, LlmTokenLimit, COALESCE(LatestRawRef, RawRef)
                        FROM Posts
                        WHERE Platform = $p AND LlmAttemptedAt IS NOT NULL
                          AND IngestReadyAt IS NOT NULL
                          AND (Hidden = 0 OR HiddenBy = 'llm')
                          AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision OR LlmTokenLimit IS NOT NULL)
                        """;
                    cmd.Parameters.AddWithValue("$p", plat);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var error = r.IsDBNull(1) ? null : r.GetString(1);
                        var at = SqliteDates.Parse(r.GetString(3));
                        if (error is null && at > staleCutoff) continue;
                        var rref = r.IsDBNull(5) ? null : r.GetString(5);
                        if (rref is not null) rawRefs.Add(rref);
                        postIssues.Add((r.GetInt64(0), plat, "judge", error, r.GetInt32(2), at, r.IsDBNull(4) ? null : r.GetInt32(4), rref));
                    }
                }
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = """
                        SELECT Id, SummaryError, SummaryFailures, SummaryAttemptedAt, COALESCE(LatestRawRef, RawRef)
                        FROM Posts
                        WHERE Platform = $p AND SummaryAttemptedAt IS NOT NULL
                          AND IngestReadyAt IS NOT NULL
                          AND Hidden = 0
                          AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)
                        """;
                    cmd.Parameters.AddWithValue("$p", plat);
                    await using var r = await cmd.ExecuteReaderAsync(ct);
                    while (await r.ReadAsync(ct))
                    {
                        var error = r.IsDBNull(1) ? null : r.GetString(1);
                        var at = SqliteDates.Parse(r.GetString(3));
                        if (error is null && at > staleCutoff) continue;
                        var rref = r.IsDBNull(4) ? null : r.GetString(4);
                        if (rref is not null) rawRefs.Add(rref);
                        postIssues.Add((r.GetInt64(0), plat, "summary", error, r.GetInt32(2), at, null, rref));
                    }
                }
            }
            // Keep per-post ordering consistent (Id ASC, judge before summary).
            postIssues.Sort((a, b) => { int c = a.Id.CompareTo(b.Id); return c != 0 ? c : string.CompareOrdinal(a.Task, b.Task); });

            var origins = new Dictionary<string, long?>(StringComparer.Ordinal);
            if (rawRefs.Count > 0)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT Path, RunId FROM RawSnapshots WHERE Path IN (SELECT value FROM json_each($refs))";
                cmd.Parameters.AddWithValue("$refs", JsonSerializer.Serialize(rawRefs));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) origins[r.GetString(0)] = r.IsDBNull(1) ? null : r.GetInt64(1);
            }
            foreach (var item in postIssues)
            {
                bool judge = item.Task == "judge";
                var stub = judge ? new Post { LlmTokenLimit = item.TokenLimit, LlmAttemptedAt = item.AttemptedAt } : null;
                var budget = judge ? JudgmentRetry.Budget(stub!, s.Config.Llm) : s.Config.Llm.SummaryMaxTokens;
                var retry = budget is null ? null : new[] { judge ? JudgmentRetry.After(stub!, s.Config.Llm) : item.AttemptedAt?.AddMinutes(30), Delay($"model:{hash}:{item.Task}:not_before"), Delay("process:not_before") }.Max();
                long? runId = item.RawRef is not null && origins.TryGetValue(item.RawRef, out var rid) ? rid : null;
                baseIssues.Add(new(item.Platform, $"post #{item.Id} · {item.Task}", item.Error ?? "Attempt has no current result; worker may still be running, interrupted, or from an older build", item.Failures, item.AttemptedAt, retry, budget is null ? "Automatic token retries exhausted or disabled; raise llm.token_retry_budgets/max_tokens or explicitly rescore" : State(item.Platform, retry, true) + (judge && item.TokenLimit != null ? $"; next max_tokens={budget}" : ""))
                { Disposition = budget is null ? RecoveryDisposition.Attention : Disposition(item.Platform, true), PostId = item.Id, RunId = runId, NextTokenBudget = judge && item.TokenLimit != null ? budget : null });
            }

            // Positive evidence of capture health. A blocked raw has its own row above; this row covers the case
            // where a changed feed passes nothing through the capture gate, so there is no raw to show.
            foreach (var p in Platforms.All.Where(p => (platform == null || p == platform) && s.Config.Enabled(p)))
                if (await CaptureHealth.SilentHomeCollects(db, p, ct) is { Length: >= CaptureHealth.SilentCollects } silent)
                    baseIssues.Add(new(p, "home collects", $"The last {silent.Length} home collects found no posts", silent.Length, null, null, "The feed shape or the capture gate may have changed. Inspect the raw diagnostics of the last collect runs and the browser session through noVNC.") { RunId = silent[0] });

            // A tool the processing child could not start (plan 10). An absent key means no scheduled run yet, which is
            // not an alarm. yt-dlp missing is an attention row only while video context needs it; the download fallback
            // waits for the tool and is a notice. ffmpeg missing is a notice while vision is on: vision still sends the
            // original image.
            if (platform is null) foreach (var tool in ToolHealth.Tools)
                if (await ToolHealth.Read(db, tool, ct) is { Status: not "ok" } state)
                {
                    bool attention = tool == "yt-dlp" && s.Config.VideoContext.Enabled, notice = tool == "yt-dlp" || s.Config.Llm.Vision;
                    if (!attention && !notice) continue;
                    var problem = $"{tool} {(state.Status == "missing" ? "is not installed or not on PATH" : "did not answer a version call: " + state.Error)}";
                    var recovery = (tool == "yt-dlp" ? "Install yt-dlp (deployment 16.2) or set video_context.enabled to false. Pending video fallbacks wait for the tool. " : "Install ffmpeg (deployment 16.2). Until then vision sends the original image. ")
                        + "The next processing run clears this row; press Process pending work on the AI processing page to check now.";
                    baseIssues.Add(new("tools", tool, problem, 1, state.At, null, recovery, !attention) { Disposition = attention ? RecoveryDisposition.Attention : RecoveryDisposition.Informational });
                }
        }

        // Load recent non-deleted raw candidates once using IX_RawSnapshots_Platform_CapturedAt, then resolve
        // supersession in bulk for only their distinct capture tokens instead of evaluating a 4-level correlated
        // subquery three times over RawSnapshots, TimelineVisits and Runs.
        var cutoff = Clock.Now.AddDays(-RawIssueDays).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        var candidates = new List<RawCandidate>();
        foreach (var plat in platform is null ? Platforms.All : [platform])
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = loadRawText
                ? "SELECT Id, Platform, RunId, CapturedAt, Parsed, AttemptedAt, Error, Warning, BlockedParserVersion, CaptureToken FROM RawSnapshots WHERE Platform = $p AND CapturedAt > $cutoff AND Deleted = 0 AND ((Parsed = 0 AND Error IS NOT NULL) OR (Parsed != 0 AND Warning IS NOT NULL))"
                : "SELECT Id, Platform, RunId, CapturedAt, Parsed, AttemptedAt, NULL, NULL, BlockedParserVersion, CaptureToken FROM RawSnapshots WHERE Platform = $p AND CapturedAt > $cutoff AND Deleted = 0 AND ((Parsed = 0 AND Error IS NOT NULL) OR (Parsed != 0 AND Warning IS NOT NULL))";
            cmd.Parameters.AddWithValue("$p", plat);
            cmd.Parameters.AddWithValue("$cutoff", cutoff);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
                candidates.Add(new(r.GetInt64(0), r.GetString(1), r.IsDBNull(2) ? null : r.GetInt64(2), SqliteDates.Parse(r.GetString(3)), r.GetInt32(4) != 0,
                    r.IsDBNull(5) ? null : SqliteDates.Parse(r.GetString(5)), r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7),
                    r.IsDBNull(8) ? null : r.GetInt32(8), r.IsDBNull(9) ? null : r.GetString(9)));
        }

        if (candidates.Count == 0)
            return new(hash, values, baseIssues, [], [], [], []);

        var supersededTokens = await FindSupersededTokens(conn, tx, candidates, ct);
        var eligibleRaws = candidates.Where(c => c.CaptureToken is null || !supersededTokens.Contains((c.Platform, c.CaptureToken))).ToArray();
        if (eligibleRaws.Length == 0)
            return new(hash, values, baseIssues, [], [], [], []);

        var eligibleIds = eligibleRaws.Select(r => r.Id).ToArray();
        var diags = new List<DiagRow>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT RawSnapshotId, Fingerprint, Code, Impact FROM RawDiagnostics WHERE RawSnapshotId IN (SELECT value FROM json_each($ids))";
            cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(eligibleIds));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) diags.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }

        // An unrecognized response is a coverage risk only when its visit observed no posts. The visit row is the
        // evidence; a home page has no visit row, so its run stands in. Without either, the risk stays assumed.
        var blockedRaws = eligibleRaws.Where(r => !r.Parsed && r.BlockedParserVersion == PayloadParser.Version).ToArray();
        var visitPosts = new Dictionary<string, int>(StringComparer.Ordinal);
        var runPosts = new Dictionary<long, int>();
        if (blockedRaws.Length > 0)
        {
            var tokens = blockedRaws.Select(r => r.CaptureToken).OfType<string>().Distinct().ToArray();
            if (tokens.Length > 0)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT CaptureToken, SUM(PostsFound) FROM TimelineVisits WHERE CaptureToken IN (SELECT value FROM json_each($tokens)) GROUP BY CaptureToken";
                cmd.Parameters.AddWithValue("$tokens", JsonSerializer.Serialize(tokens));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) visitPosts[r.GetString(0)] = r.GetInt32(1);
            }
            var runIds = blockedRaws.Select(r => r.RunId).OfType<long>().Distinct().ToArray();
            if (runIds.Length > 0)
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = tx;
                cmd.CommandText = "SELECT Id, PostsFound FROM Runs WHERE Id IN (SELECT value FROM json_each($ids))";
                cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(runIds));
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) runPosts[r.GetInt64(0)] = r.GetInt32(1);
            }
        }
        return new(hash, values, baseIssues, eligibleRaws, diags.ToArray(), visitPosts, runPosts);
    }

    static async Task<HashSet<(string Platform, string Token)>> FindSupersededTokens(SqliteConnection conn, SqliteTransaction? tx, List<RawCandidate> candidates, CancellationToken ct)
    {
        var superseded = new HashSet<(string Platform, string Token)>();
        var candidatePairs = new HashSet<(string Platform, string Token)>();
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in candidates)
            if (c.CaptureToken is not null) { candidatePairs.Add((c.Platform, c.CaptureToken)); tokens.Add(c.CaptureToken); }
        if (tokens.Count == 0) return superseded;

        var oldVisits = new List<(string Platform, string Token, long? AuthorId, string Url, DateTime At, long RunId)>();
        var authorIds = new HashSet<long>();
        var legacyUrls = new HashSet<string>(StringComparer.Ordinal);
        DateTime minAt = DateTime.MaxValue;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Platform, CaptureToken, AuthorId, Url, At, RunId FROM TimelineVisits WHERE CaptureToken IN (SELECT value FROM json_each($tokens))";
            cmd.Parameters.AddWithValue("$tokens", JsonSerializer.Serialize(tokens));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var plat = r.GetString(0); var tok = r.GetString(1);
                if (!candidatePairs.Contains((plat, tok))) continue;
                long? aid = r.IsDBNull(2) ? null : r.GetInt64(2);
                var url = r.GetString(3); var at = SqliteDates.Parse(r.GetString(4)); var runId = r.GetInt64(5);
                oldVisits.Add((plat, tok, aid, url, at, runId));
                if (aid is { } id) authorIds.Add(id); else legacyUrls.Add(url);
                if (at < minAt) minAt = at;
            }
        }
        if (oldVisits.Count == 0) return superseded;

        // Fetch only newer rendered/captured visits with posts for the affected authors or legacy URLs.
        var nextCandidates = new List<(string Platform, long? AuthorId, string Url, DateTime At, long RunId, string CaptureToken)>();
        var nextRunIds = new HashSet<long>();
        var nextTokens = new HashSet<string>(StringComparer.Ordinal);
        var minAtStr = minAt.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
        if (authorIds.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Platform, AuthorId, Url, At, RunId, CaptureToken
                FROM TimelineVisits
                WHERE Status = 'rendered' AND CaptureStatus = 'captured' AND PostsFound > 0 AND CaptureToken IS NOT NULL
                  AND At > $minAt AND AuthorId IN (SELECT value FROM json_each($aids))
                """;
            cmd.Parameters.AddWithValue("$minAt", minAtStr);
            cmd.Parameters.AddWithValue("$aids", JsonSerializer.Serialize(authorIds));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var runId = r.GetInt64(4); var tok = r.GetString(5);
                nextCandidates.Add((r.GetString(0), r.GetInt64(1), r.GetString(2), SqliteDates.Parse(r.GetString(3)), runId, tok));
                nextRunIds.Add(runId); nextTokens.Add(tok);
            }
        }
        if (legacyUrls.Count > 0)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Platform, Url, At, RunId, CaptureToken
                FROM TimelineVisits
                WHERE Status = 'rendered' AND CaptureStatus = 'captured' AND PostsFound > 0 AND CaptureToken IS NOT NULL
                  AND At > $minAt AND AuthorId IS NULL AND Url IN (SELECT value FROM json_each($urls))
                """;
            cmd.Parameters.AddWithValue("$minAt", minAtStr);
            cmd.Parameters.AddWithValue("$urls", JsonSerializer.Serialize(legacyUrls));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                var runId = r.GetInt64(3); var tok = r.GetString(4);
                nextCandidates.Add((r.GetString(0), null, r.GetString(1), SqliteDates.Parse(r.GetString(2)), runId, tok));
                nextRunIds.Add(runId); nextTokens.Add(tok);
            }
        }
        if (nextCandidates.Count == 0) return superseded;

        var finishedRuns = new HashSet<long>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT Id FROM Runs WHERE FinishedAt IS NOT NULL AND Status <> 'running' AND Id IN (SELECT value FROM json_each($ids))";
            cmd.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(nextRunIds));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) finishedRuns.Add(r.GetInt64(0));
        }
        var cleanTokens = new HashSet<(string Platform, string Token)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Platform, CaptureToken
                FROM RawSnapshots
                WHERE CaptureToken IN (SELECT value FROM json_each($tokens))
                GROUP BY Platform, CaptureToken
                HAVING SUM(CASE WHEN Parsed != 0 THEN 1 ELSE 0 END) > 0
                   AND SUM(CASE WHEN Parsed = 0 THEN 1 ELSE 0 END) = 0
                """;
            cmd.Parameters.AddWithValue("$tokens", JsonSerializer.Serialize(nextTokens));
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) cleanTokens.Add((r.GetString(0), r.GetString(1)));
        }

        // Keep up to the two newest distinct-RunId clean visits per author or legacy URL so a same-RunId newest visit
        // cannot mask an earlier clean visit from a different run that still postdates the old visit.
        static void RecordTop2<TKey>(Dictionary<TKey, ((DateTime At, long RunId) First, (DateTime At, long RunId)? Second)> map, TKey key, DateTime at, long runId) where TKey : notnull
        {
            if (!map.TryGetValue(key, out var cur)) { map[key] = ((at, runId), null); return; }
            if (cur.First.RunId == runId) { if (at > cur.First.At) map[key] = ((at, runId), cur.Second); return; }
            if (cur.Second is { } sec && sec.RunId == runId)
            {
                if (at > cur.First.At) map[key] = ((at, runId), cur.First);
                else if (at > sec.At) map[key] = (cur.First, (at, runId));
                return;
            }
            if (at > cur.First.At) map[key] = ((at, runId), cur.First);
            else if (cur.Second is null || at > cur.Second.Value.At) map[key] = (cur.First, (at, runId));
        }
        static bool HasNewerOtherRun<TKey>(Dictionary<TKey, ((DateTime At, long RunId) First, (DateTime At, long RunId)? Second)> map, TKey key, DateTime oldAt, long oldRunId) where TKey : notnull
            => map.TryGetValue(key, out var cur) && (cur.First.RunId != oldRunId ? cur.First.At > oldAt : cur.Second is { } sec && sec.At > oldAt);

        var cleanByAuthor = new Dictionary<(string Platform, long AuthorId), ((DateTime At, long RunId) First, (DateTime At, long RunId)? Second)>();
        var cleanByUrl = new Dictionary<(string Platform, string Url), ((DateTime At, long RunId) First, (DateTime At, long RunId)? Second)>();
        foreach (var n in nextCandidates)
        {
            if (!finishedRuns.Contains(n.RunId) || !cleanTokens.Contains((n.Platform, n.CaptureToken))) continue;
            if (n.AuthorId is { } aid) RecordTop2(cleanByAuthor, (n.Platform, aid), n.At, n.RunId);
            else RecordTop2(cleanByUrl, (n.Platform, n.Url), n.At, n.RunId);
        }
        foreach (var old in oldVisits)
        {
            bool match = old.AuthorId is { } aid
                ? HasNewerOtherRun(cleanByAuthor, (old.Platform, aid), old.At, old.RunId)
                : HasNewerOtherRun(cleanByUrl, (old.Platform, old.Url), old.At, old.RunId);
            if (match) superseded.Add((old.Platform, old.Token));
        }
        return superseded;
    }

    static RecoveryIssue[] BuildIssues(RecoveryContext ctx, InstanceSnapshot s, bool grouped, string? fingerprint, int page, bool summaryOnly)
    {
        var issues = new List<RecoveryIssue>(ctx.BaseIssues.Count + 16);
        if (fingerprint is null) issues.AddRange(ctx.BaseIssues);
        if (ctx.EligibleRaws.Length == 0) return issues.ToArray();

        DateTime? Delay(string key) => DateTime.TryParse(ctx.KvValues.GetValueOrDefault(key), out var at) ? at.ToUniversalTime() : null;
        string State(string p, DateTime? at) => !s.Config.Scheduler.Enabled ? "Automatic recovery paused: scheduler disabled" : !s.Config.Enabled(p) ? "Automatic recovery paused: platform disabled" : at > Clock.Now ? "Automatic retry after delay" : "Due for automatic recovery";
        RecoveryDisposition Disposition(string p) => !s.Config.Scheduler.Enabled || !s.Config.Enabled(p) ? RecoveryDisposition.Paused : RecoveryDisposition.Automatic;

        var rawById = ctx.EligibleRaws.ToDictionary(r => r.Id);
        var hasDiag = new HashSet<long>(ctx.Diagnostics.Select(d => d.RawSnapshotId));
        var groupedRows = ctx.Diagnostics
            .GroupBy(d => { var r = rawById[d.RawSnapshotId]; return (r.Platform, d.Fingerprint, d.Impact, d.Code, r.Parsed, r.BlockedParserVersion); })
            .Select(g => new RawGroup(g.Key.Fingerprint, g.Key.Code, g.Key.Impact, g.Max(d => d.RawSnapshotId), g.Select(d => d.RawSnapshotId).Distinct().Count(), g.Select(d => rawById[d.RawSnapshotId].RunId).Where(id => id != null).Distinct().Count(), g.Min(d => rawById[d.RawSnapshotId].CapturedAt), g.Max(d => rawById[d.RawSnapshotId].CapturedAt)))
            .Concat(ctx.EligibleRaws.Where(r => !hasDiag.Contains(r.Id)).Select(r => new RawGroup("legacy-" + r.Id, "legacy", r.Parsed ? "auxiliary" : "unknown_or_content", r.Id, 1, 1, r.CapturedAt, r.CapturedAt)))
            .OrderByDescending(g => g.Last).ThenBy(g => g.Fingerprint, StringComparer.Ordinal).ToArray();
        if (grouped && !summaryOnly) groupedRows = groupedRows.Skip((Math.Max(1, page) - 1) * 50).Take(50).ToArray();

        IEnumerable<RawCandidate> selection = ctx.EligibleRaws;
        DiagRow? detail = null;
        if (fingerprint is not null)
        {
            var fpIds = ctx.Diagnostics.Where(d => d.Fingerprint == fingerprint).Select(d => d.RawSnapshotId).ToHashSet();
            selection = selection.Where(r => fingerprint == "legacy-" + r.Id || fpIds.Contains(r.Id));
            detail = ctx.Diagnostics.FirstOrDefault(d => d.Fingerprint == fingerprint);
        }
        else if (grouped)
        {
            var groupIds = groupedRows.Select(g => g.Id).ToHashSet();
            selection = selection.Where(r => groupIds.Contains(r.Id));
        }
        selection = selection.OrderByDescending(r => r.Id);
        if (fingerprint is not null) selection = selection.Skip((Math.Max(1, page) - 1) * 50).Take(50);
        var groupsByRawId = grouped && fingerprint is null ? groupedRows.ToLookup(g => g.Id) : null;
        bool PostsObserved(RawCandidate r) => r.CaptureToken is { } token && ctx.VisitPosts.TryGetValue(token, out var found) ? found > 0 : r.RunId is { } run && ctx.RunPosts.GetValueOrDefault(run) > 0;
        var processDelay = Delay("process:not_before");

        foreach (var r in selection)
        {
            var retry = r.AttemptedAt?.AddMinutes(30) is { } a ? (processDelay > a ? processDelay : a) : processDelay;
            var rawGroups = groupsByRawId is not null ? groupsByRawId[r.Id] : [];
            foreach (var group in rawGroups.Cast<RawGroup?>().DefaultIfEmpty(null))
            {
                var count = group?.Count ?? 1;
                var informational = group is not null ? group.Impact is "auxiliary" or "partial" : r.Parsed;
                var blocked = !r.Parsed && r.BlockedParserVersion == PayloadParser.Version;
                var backlog = blocked && (group?.Code ?? detail?.Code) == "unknown_response" && (group?.Impact ?? detail?.Impact) == "unknown_or_content" && PostsObserved(r);
                var recovery = r.Parsed ? "Recorded upstream warning; no replay needed" : blocked ? "Needs parser update; saved raw will retry after parser upgrade (explicit reparse also available)" : State(r.Platform, retry);
                if (!informational && group?.Impact == "content") recovery = "Identifiable post content may be missing. " + recovery;
                else if (backlog) recovery = "Unrecognized response beside captured posts; no post is known to be missing. " + recovery;
                else if (!informational && group?.Impact == "unknown_or_content") recovery = "Unrecognized response; content impact unresolved. " + recovery;
                issues.Add(new(r.Platform, $"raw #{r.Id} · run #{r.RunId}", summaryOnly ? group?.Code ?? "capture_issue" : r.Error ?? r.Warning ?? "Waiting for ingest", 0, r.AttemptedAt, r.Parsed || blocked ? null : retry,
                    informational ? "Recorded upstream warning; no replay needed for this issue" : recovery, informational)
                {
                    Disposition = informational ? RecoveryDisposition.Informational : backlog ? RecoveryDisposition.Backlog : blocked ? RecoveryDisposition.Compatibility : Disposition(r.Platform),
                    RunId = r.RunId, RawId = r.Id, CaptureCount = count, Fingerprint = group?.Fingerprint ?? fingerprint, Code = group?.Code,
                    RunCount = group?.Runs ?? 1, FirstSeen = group?.First, LastSeen = group?.Last
                });
            }
        }
        return issues.ToArray();
    }
}
