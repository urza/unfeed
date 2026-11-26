using System.Text;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;
public static class Reports
{
    public static async Task<string> Rules(DbFactory factory, InstanceSnapshot s)
    {
        await using var db = factory.Open(); var authors = await db.Authors.AsNoTracking().ToListAsync(); var resolved = new ResolvedRules(authors, s); var b = new StringBuilder();
        b.AppendLine("Rules: type → audience → keyword → mute → model. Historical changes require refilter/rescore."); b.AppendLine("Blocked types: " + string.Join(", ", s.Config.Filters.BlockedTypes)); b.AppendLine($"Audience: {s.Config.Filters.Audience}; tag exception: {s.Config.Filters.FriendTagException}"); b.AppendLine("Always-show bypasses: " + string.Join(", ", s.Config.Filters.AlwaysShowBypasses) + "; mutes still win.");
        b.AppendLine($"Timezone: {s.Config.Zone.Id}" + (s.Config.Timezone is not null && s.Config.Timezone != s.Config.Zone.Id ? " (unknown configured zone; process fallback)" : ""));
        foreach (var (kind, entries) in new[] { ("show", s.Preferences.Shows), ("mute", s.Preferences.Mutes) }.Concat(s.Config.Platforms.Select(p => ("close friends " + p.Key, p.Value.CloseFriends)))) foreach (var entry in entries) { var matches = resolved.Resolve(entry).ToArray(); b.AppendLine($"{kind}: {entry} → {(matches.Length == 0 ? "unmatched" : string.Join(", ", matches.Select(a => $"#{a.Id} {a.DisplayName}")))}{(matches.Length > 1 ? " (ambiguous; pin refs)" : "")}"); }
        foreach (var (platform, settings) in s.Config.Platforms) foreach (var entry in settings.HomeTimelineAuthors)
        {
            var matches = resolved.Resolve(entry, platform).ToArray();
            b.AppendLine($"home timeline {platform}: {entry} → {(matches.Length == 0 ? "unmatched" : string.Join(", ", matches.Select(a => $"#{a.Id} {a.DisplayName}")))}{(matches.Length > 1 ? " (ambiguous; pin refs)" : "")}");
        }
        foreach (var keyword in s.Preferences.Keywords) b.AppendLine("Keyword: " + keyword);
        foreach (var line in s.Preferences.Policy) b.AppendLine("Model policy: " + line);
        foreach (var c in s.Taxonomy.Categories) b.AppendLine($"Category {c.Key}: {c.Definition}; close friends only: {string.Join(", ", c.CloseFriendsOnly)}");
        foreach (var v in s.Taxonomy.Views) b.AppendLine($"View {v.Key} ({v.Label}): {(v.Category is not null ? "category=" + v.Category + (v.Authors is { } selectedAuthors ? " AND authors=" + string.Join(", ", selectedAuthors) : "") : v.Authors is { } a ? "authors=" + string.Join(", ", a) : v.Union is { } u ? "union=" + string.Join(", ", u) : $"rare={v.Rare!.MaxPosts}/{v.Rare.WindowDays} days")}");
        b.AppendLine("View all: built in; author views grant no bypass. Thumbs record feedback and never filter."); return b.ToString();
    }
    public static async Task<string> Status(DbFactory factory, InstanceSnapshot s)
    {
        await using var db = factory.Open(); await db.Database.OpenConnectionAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var b = new StringBuilder();
        var states = await db.PlatformStates.AsNoTracking().ToDictionaryAsync(p => p.Platform);
        var postStats = new Dictionary<string, (int Total, int Visible, int Unscored)>(StringComparer.Ordinal);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT Platform,
                       COUNT(*),
                       SUM(CASE WHEN Hidden = 0 THEN 1 ELSE 0 END),
                       SUM(CASE WHEN Hidden = 0 AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision) THEN 1 ELSE 0 END)
                FROM Posts GROUP BY Platform
                """;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) postStats[r.GetString(0)] = (r.GetInt32(1), r.GetInt32(2), r.GetInt32(3));
        }
        var authorStats = await db.Authors.GroupBy(a => a.Platform).Select(g => new { Platform = g.Key, Total = g.Count(), Friends = g.Count(a => a.IsFriend) }).ToDictionaryAsync(x => x.Platform);
        foreach (var platform in Platforms.All)
        {
            var state = states.GetValueOrDefault(platform);
            var ps = postStats.GetValueOrDefault(platform);
            var aus = authorStats.GetValueOrDefault(platform);
            b.AppendLine($"{platform}: {(s.Config.Enabled(platform) ? "enabled" : "paused")}; posts={ps.Total}, visible={ps.Visible}, authors={aus?.Total ?? 0}, friends={aus?.Friends ?? 0}, needs re-login={state?.NeedsRelogin == true}; last collect={state?.LastRunFinishedAt:O}");
        }
        foreach (var run in await db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(15).ToListAsync()) b.AppendLine($"run #{run.Id} {run.Kind}/{run.Phase} {run.Platform} {run.Mode}: {run.Status}; pid={run.ProcessPid}, OS start={run.ProcessStartedAt:O}, request={run.RequestId}, started={run.StartedAt:O}, error={run.Error}");
        foreach (var r in await db.RunRequests.AsNoTracking().OrderByDescending(r => r.Id).Take(10).ToListAsync()) b.AppendLine($"request #{r.Id} {r.Kind} {r.Platform}: {r.Status}; child={r.ChildPid}/{r.ChildStartedAt:O}; exit={r.ExitCode}; {r.Note}");
        int unscoredVisible = postStats.Values.Sum(x => x.Unscored);
        b.AppendLine($"Model: {(s.Config.Llm.Enabled ? s.Config.Llm.Model + " at " + Prompts.Endpoint(s.Config.Llm.BaseUrl) : "disabled")}; unscored visible={unscoredVisible}; categories={s.Taxonomy.Categories.Length}; policy lines={s.Preferences.Policy.Length}");
        int pendingSummaries;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM Posts WHERE Hidden = 0 AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)";
            pendingSummaries = Convert.ToInt32(await cmd.ExecuteScalarAsync());
        }
        b.AppendLine($"Processing: unparsed raws={await db.RawSnapshots.CountAsync(r => !r.Parsed && !r.Deleted)}, unfinished ingest={await db.Posts.CountAsync(p => p.IngestReadyAt == null)}, summaries={pendingSummaries}, pending videos={await db.Media.CountAsync(m => m.Kind == "video" && m.Path == null && m.PrunedAt == null)}, pending hearts={await db.Likes.CountAsync(l => l.State == "pending")}");
        foreach (var row in await CoverageQuery.Read(db, s)) { b.AppendLine($"coverage {row.Platform} {row.Name}: {row.Status}; last attempt={row.LastAttempt:O}; last capture={row.LastCapture:O}; {(row.Stale ? "not checked recently" : "recent capture")}; range={row.OldestPost:O}..{row.NewestPost:O}"); if (row.Failures >= 2) b.AppendLine($"WARNING timeline: {row.Name}: {row.Failures} incomplete visits; {row.Note}; screenshot={row.Screenshot}"); }
        foreach (var run in await db.Runs.AsNoTracking().Where(r => r.Status == "running" && r.Kind == "process").ToArrayAsync()) b.AppendLine($"live processing #{run.Id}: {run.StatsJson ?? "starting"}");
        foreach (var e in await IdentityStore.Events(db, 50, "alias_conflict")) b.AppendLine($"WARNING identity: {e.Event.Key} stays with \"{e.Kept?.DisplayName}\" ({Platforms.Prefix(e.Event.Platform)}:{e.Kept?.PlatformAuthorId}); seen with id {e.Event.ObservedId} (\"{e.OtherName}\") {e.Event.Count}x, last {e.Event.LastAt:O}; a preference entry for either person should use the id ref");
        var hash = Prompts.ConfigurationHash(s); var now = Clock.Now; var retryCutoff = now.AddMinutes(-30);
        var tokenCutoff = now.AddMinutes(-s.Config.Llm.TokenRetryDelayMinutes);
        var ceiling = Math.Max(s.Config.Llm.MaxTokens, s.Config.Llm.TokenRetryBudgets.DefaultIfEmpty(0).Max());
        var kv = await db.Kv.AsNoTracking().Where(k => k.Key.StartsWith("model:" + hash) || k.Key.StartsWith("sweep:") || k.Key == "disk:summary").ToDictionaryAsync(k => k.Key, k => k.Value);
        foreach (var platform in Platforms.All)
        {
            // Match IX_Posts_JudgeQueue and IX_Posts_SummaryQueue partial index filters directly, projecting only queue columns.
            int judgeCount = 0, judgeDue = 0; DateTime? judgeOldest = null, judgeRetry = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT CapturedAt, LlmAttemptedAt, LlmTokenLimit
                    FROM Posts
                    WHERE Platform = $p AND IngestReadyAt IS NOT NULL
                      AND (Hidden = 0 OR HiddenBy = 'llm')
                      AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision OR LlmTokenLimit IS NOT NULL)
                    """;
                cmd.Parameters.AddWithValue("$p", platform);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    judgeCount++;
                    var cap = SqliteDates.Parse(r.GetString(0));
                    if (judgeOldest is null || cap < judgeOldest) judgeOldest = cap;
                    DateTime? att = r.IsDBNull(1) ? null : SqliteDates.Parse(r.GetString(1));
                    int? lim = r.IsDBNull(2) ? null : r.GetInt32(2);
                    if (lim is null ? (att is null || att <= retryCutoff) : (lim < ceiling && (att is null || att <= tokenCutoff))) judgeDue++;
                    var after = JudgmentRetry.After(new Post { LlmAttemptedAt = att, LlmTokenLimit = lim }, s.Config.Llm);
                    if (after > now && (judgeRetry is null || after < judgeRetry)) judgeRetry = after;
                }
            }
            b.AppendLine($"{platform}/judge: pending={judgeCount}, due={judgeDue}, oldest={judgeOldest:O}, retry={judgeRetry:O}; task backoff={kv.GetValueOrDefault($"model:{hash}:judge:not_before") ?? "none"}; {(s.Config.Enabled(platform) ? s.Config.Llm.Enabled ? "model enabled" : "processing model disabled" : "platform paused")}");

            int sumCount = 0, sumDue = 0; DateTime? sumOldest = null, sumRetry = null;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT CapturedAt, SummaryAttemptedAt
                    FROM Posts
                    WHERE Platform = $p AND IngestReadyAt IS NOT NULL
                      AND Hidden = 0
                      AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)
                    """;
                cmd.Parameters.AddWithValue("$p", platform);
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                {
                    sumCount++;
                    var cap = SqliteDates.Parse(r.GetString(0));
                    if (sumOldest is null || cap < sumOldest) sumOldest = cap;
                    DateTime? att = r.IsDBNull(1) ? null : SqliteDates.Parse(r.GetString(1));
                    if (att is null || att <= retryCutoff) sumDue++;
                    var after = att?.AddMinutes(30);
                    if (after > now && (sumRetry is null || after < sumRetry)) sumRetry = after;
                }
            }
            b.AppendLine($"{platform}/summary: pending={sumCount}, due={sumDue}, oldest={sumOldest:O}, retry={sumRetry:O}; task backoff={kv.GetValueOrDefault($"model:{hash}:summary:not_before") ?? "none"}; {(s.Config.Enabled(platform) ? s.Config.Llm.Enabled ? "model enabled" : "processing model disabled" : "platform paused")}");
        }
        foreach (var group in (await db.SweepTargets.AsNoTracking().ToListAsync()).GroupBy(t => new { t.Platform, t.CycleId })) if (kv.GetValueOrDefault($"sweep:{group.Key.Platform}:cycle") == group.Key.CycleId) b.AppendLine($"sweep {group.Key.Platform}/{group.Key.CycleId}: attempted={group.Count(t => t.State is "done" or "skipped" or "retry")}/{group.Count()}, remaining={group.Count(t => t.State is "pending" or "visiting" or "retry")}");
        foreach (var issue in await RecoveryQuery.Read(db, s, grouped: true, summaryOnly: true)) b.AppendLine($"recovery {issue.Platform}/{issue.Item}: {issue.Problem}; captures={issue.CaptureCount}; signature={issue.Fingerprint}; attempts={issue.Attempts}; retry={issue.RetryAfter:O}; {issue.Recovery}");
        b.AppendLine(kv.GetValueOrDefault("disk:summary") ?? "disk: not measured"); b.AppendLine(await Maintenance.BackupLine(factory.Paths, db, default)); b.AppendLine(await ToolHealth.Line(db)); return b.ToString();
    }
}
