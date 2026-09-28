using System.Text;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Queries;
public static class Reports
{
    public static async Task<string> Rules(DbFactory factory, InstanceSnapshot s)
    {
        await using var db = factory.Open(); var authors = await db.Authors.AsNoTracking().ToListAsync(); var b = new StringBuilder();
        b.AppendLine("Rules: type → audience → keyword → mute → model. Historical changes require refilter/rescore."); b.AppendLine("Blocked types: " + string.Join(", ", s.Config.Filters.BlockedTypes)); b.AppendLine($"Audience: {s.Config.Filters.Audience}; tag exception: {s.Config.Filters.FriendTagException}"); b.AppendLine("Always-show bypasses: " + string.Join(", ", s.Config.Filters.AlwaysShowBypasses) + "; mutes still win.");
        b.AppendLine($"Timezone: {s.Config.Zone.Id}" + (s.Config.Timezone is not null && s.Config.Timezone != s.Config.Zone.Id ? " (unknown configured zone; process fallback)" : ""));
        foreach (var (kind, entries) in new[] { ("show", s.Preferences.Shows), ("mute", s.Preferences.Mutes) }.Concat(s.Config.Platforms.Select(p => ("close friends " + p.Key, p.Value.CloseFriends)))) foreach (var entry in entries) { var matches = Identity.Resolve(entry, authors).ToArray(); b.AppendLine($"{kind}: {entry} → {(matches.Length == 0 ? "unmatched" : string.Join(", ", matches.Select(a => $"#{a.Id} {a.DisplayName}")))}{(matches.Length > 1 ? " (ambiguous; pin refs)" : "")}"); }
        foreach (var keyword in s.Preferences.Keywords) b.AppendLine("Keyword: " + keyword);
        foreach (var line in s.Preferences.Policy) b.AppendLine("Model policy: " + line);
        foreach (var c in s.Taxonomy.Categories) b.AppendLine($"Category {c.Key}: {c.Definition}; close friends only: {string.Join(", ", c.CloseFriendsOnly)}");
        foreach (var v in s.Taxonomy.Views) b.AppendLine($"View {v.Key} ({v.Label}): {(v.Category is not null ? "category=" + v.Category + (v.Authors is { } selectedAuthors ? " AND authors=" + string.Join(", ", selectedAuthors) : "") : v.Authors is { } a ? "authors=" + string.Join(", ", a) : v.Union is { } u ? "union=" + string.Join(", ", u) : $"rare={v.Rare!.MaxPosts}/{v.Rare.WindowDays} days")}");
        b.AppendLine("View all: built in; author views grant no bypass. Thumbs record feedback and never filter."); return b.ToString();
    }
    public static async Task<string> Status(DbFactory factory, InstanceSnapshot s)
    {
        await using var db = factory.Open(); var b = new StringBuilder();
        foreach (var platform in Platforms.All)
        {
            var state = await db.PlatformStates.FindAsync(platform); var posts = db.Posts.Where(p => p.Platform == platform);
            b.AppendLine($"{platform}: {(s.Config.Enabled(platform) ? "enabled" : "paused")}; posts={await posts.CountAsync()}, visible={await posts.CountAsync(p => !p.Hidden)}, authors={await db.Authors.CountAsync(a => a.Platform == platform)}, friends={await db.Authors.CountAsync(a => a.Platform == platform && a.IsFriend)}, needs re-login={state?.NeedsRelogin == true}; last collect={state?.LastRunFinishedAt:O}");
        }
        foreach (var run in await db.Runs.AsNoTracking().OrderByDescending(r => r.Id).Take(15).ToListAsync()) b.AppendLine($"run #{run.Id} {run.Kind}/{run.Phase} {run.Platform} {run.Mode}: {run.Status}; pid={run.ProcessPid}, OS start={run.ProcessStartedAt:O}, request={run.RequestId}, started={run.StartedAt:O}, error={run.Error}");
        foreach (var r in await db.RunRequests.AsNoTracking().OrderByDescending(r => r.Id).Take(10).ToListAsync()) b.AppendLine($"request #{r.Id} {r.Kind} {r.Platform}: {r.Status}; child={r.ChildPid}/{r.ChildStartedAt:O}; exit={r.ExitCode}; {r.Note}");
        b.AppendLine($"Model: {(s.Config.Llm.Enabled ? s.Config.Llm.Model + " at " + Prompts.Endpoint(s.Config.Llm.BaseUrl) : "disabled")}; unscored visible={await db.Posts.CountAsync(p => !p.Hidden && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision))}; categories={s.Taxonomy.Categories.Length}; policy lines={s.Preferences.Policy.Length}");
        b.AppendLine($"Processing: unparsed raws={await db.RawSnapshots.CountAsync(r => !r.Parsed && !r.Deleted)}, unfinished ingest={await db.Posts.CountAsync(p => p.IngestReadyAt == null)}, summaries={await db.Posts.CountAsync(p => !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision))}, pending videos={await db.Media.CountAsync(m => m.Kind == "video" && m.Path == null && m.PrunedAt == null)}, pending hearts={await db.Likes.CountAsync(l => l.State == "pending")}");
        foreach (var row in await CoverageQuery.Read(db, s)) { b.AppendLine($"coverage {row.Platform} {row.Name}: {row.Status}; last attempt={row.LastAttempt:O}; last capture={row.LastCapture:O}; {(row.Stale ? "not checked recently" : "recent capture")}; range={row.OldestPost:O}..{row.NewestPost:O}"); if (row.Failures >= 2) b.AppendLine($"WARNING timeline: {row.Name}: {row.Failures} incomplete visits; {row.Note}; screenshot={row.Screenshot}"); }
        foreach (var run in await db.Runs.AsNoTracking().Where(r => r.Status == "running" && r.Kind == "process").ToArrayAsync()) b.AppendLine($"live processing #{run.Id}: {run.StatsJson ?? "starting"}");
        var hash = Prompts.ConfigurationHash(s); var retryCutoff = Clock.Now.AddMinutes(-30);
        foreach (var platform in Platforms.All)
        {
            var pending = db.Posts.Where(p => p.Platform == platform && p.IngestReadyAt != null);
            foreach (var task in new[] { "judge", "summary" })
            {
                var candidates = task == "judge" ? pending.Where(p => (!p.Hidden || p.HiddenBy == "llm") && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision)) : pending.Where(p => !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision));
                var attempts = task == "judge" ? candidates.Select(p => new { p.CapturedAt, Attempt = p.LlmAttemptedAt }) : candidates.Select(p => new { p.CapturedAt, Attempt = p.SummaryAttemptedAt });
                var rows = await attempts.ToArrayAsync(); var due = rows.Count(r => r.Attempt == null || r.Attempt <= retryCutoff); var retry = rows.Where(r => r.Attempt > retryCutoff).Select(r => r.Attempt!.Value.AddMinutes(30)).Cast<DateTime?>().Min();
                b.AppendLine($"{platform}/{task}: pending={rows.Length}, due={due}, oldest={rows.Select(r => (DateTime?)r.CapturedAt).Min():O}, retry={retry:O}; task backoff={await db.Get($"model:{hash}:{task}:not_before") ?? "none"}; {(s.Config.Enabled(platform) ? s.Config.Llm.Enabled ? "model enabled" : "processing model disabled" : "platform paused")}");
            }
        }
        foreach (var group in (await db.SweepTargets.AsNoTracking().ToListAsync()).GroupBy(t => new { t.Platform, t.CycleId })) if (await db.Get($"sweep:{group.Key.Platform}:cycle") == group.Key.CycleId) b.AppendLine($"sweep {group.Key.Platform}/{group.Key.CycleId}: attempted={group.Count(t => t.State is "done" or "skipped" or "retry")}/{group.Count()}, remaining={group.Count(t => t.State is "pending" or "visiting" or "retry")}");
        foreach (var issue in await RecoveryQuery.Read(db, s)) b.AppendLine($"recovery {issue.Platform}/{issue.Item}: {issue.Problem}; attempts={issue.Attempts}; retry={issue.RetryAfter:O}; {issue.Recovery}");
        b.AppendLine(await db.Get("disk:summary") ?? "disk: not measured"); return b.ToString();
    }
}
