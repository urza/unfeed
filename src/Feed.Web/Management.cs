using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;

namespace Feed.Web;

public sealed record ProcessingCompletion(long PostId, string Platform, string Task, DateTime At);
public sealed record PersonRow(Author Author, bool Close, bool Muted, bool Always, int Posts, int Hidden, DateTime? Latest, TimelineVisit? Visit, string? Retry);
public sealed record PlatformOverview(string Platform, int Friends, int Posts, int Hidden, PlatformState? State, Run? Collection, Run? Import, Run[] Running, RunRequest[] Requests, string Next);
public sealed record ManagementPage(ManagementDocuments Documents, InstanceSnapshot Instance, PlatformOverview[] Platforms, PersonRow[] People, int PeopleCount, int Page, string Query, string Platform, string Filter, string Section, string Disk, string? DiskAt, int Pending, int Failures)
{
    public RecoveryIssue[] Recovery { get; init; } = [];
    public ProcessingCompletion[] RecentCompletions { get; init; } = [];
    public Run[] Collections { get; init; } = [];
    public TimelineVisit[] CollectionVisits { get; init; } = [];
    public DateTime UpdatedAt { get; init; } = Clock.Now;
}

public sealed class Management(InstancePaths paths, DbFactory factory, ManagementFiles documents)
{
    public static string PlatformName(string p) => p == "facebook" ? "Facebook" : "Instagram";
    public static string ModeName(string mode) => mode switch { "home" => "Home feed", "close_friends" => "Home + close friends", _ => "Everyone followed" };
    public static string RelativeTime(DateTime at, DateTime now)
    {
        var delta = at - now;
        var seconds = Math.Abs(delta.TotalSeconds);
        if (seconds < 60) return delta > TimeSpan.Zero ? "in less than a minute" : "just now";
        var (count, unit) = seconds < 3600 ? ((int)(seconds / 60), "minute")
            : seconds < 86400 ? ((int)(seconds / 3600), "hour")
            : ((int)(seconds / 86400), "day");
        var duration = $"{count} {unit}{(count == 1 ? "" : "s")}";
        return delta > TimeSpan.Zero ? $"in {duration}" : $"{duration} ago";
    }
    public static string Reference(Author a) => Identity.Keys(a).FirstOrDefault(k => k == Platforms.Prefix(a.Platform) + ":" + a.PlatformAuthorId) ?? Identity.Keys(a).FirstOrDefault() ?? throw new FormatException("This person has no usable identity reference.");
    public async Task<ManagementPage> Read(string section, string query, string platform, string filter, int page, CancellationToken ct, long? collectionId = null)
    {
        var docs = documents.Read(); var s = docs.Snapshot();
        await using var db = factory.Open();
        var authors = await db.Authors.AsNoTracking().OrderBy(a => a.DisplayName).ThenBy(a => a.Id).ToArrayAsync(ct);
        var counts = await db.Posts.GroupBy(p => p.AuthorId).Select(g => new { Id = g.Key, Total = g.Count(), Hidden = g.Count(p => p.Hidden), Latest = g.Max(p => p.PostedAt) }).ToArrayAsync(ct);
        var visits = section == "people" ? await db.TimelineVisits.AsNoTracking().Where(v => v.AuthorId != null).OrderByDescending(v => v.At).ThenByDescending(v => v.Id).ToArrayAsync(ct) : [];
        var targets = section == "people" ? await db.SweepTargets.AsNoTracking().Where(t => t.State == "retry" && db.Kv.Any(k => k.Key == "sweep:" + t.Platform + ":cycle" && k.Value == t.CycleId)).ToArrayAsync(ct) : [];
        var visiblePeople = authors.Where(a => (platform.Length == 0 || a.Platform == platform) && (query.Length == 0 || Identity.Normalize((a.DisplayName ?? "") + " " + a.RefsJson).Contains(Identity.Normalize(query)))).Select(a => {
            var rules = new RuleContext(a, authors, s); var count = counts.FirstOrDefault(c => c.Id == a.Id);
            return new PersonRow(a, rules.CloseFriend, s.Preferences.Mutes.Any(e => Identity.Matches(e, a)), s.Preferences.Shows.Any(e => Identity.Resolve(e, authors).Any(x => x.Id == a.Id)), count?.Total ?? 0, count?.Hidden ?? 0, count?.Latest, visits.FirstOrDefault(v => v.AuthorId == a.Id), targets.FirstOrDefault(t => t.AuthorId == a.Id) is { } t ? $"Retry {t.Attempts}/{TimelineCoverage.MaxAttempts}; due {t.RetryAt:u}" : null);
        }).Where(p => filter switch { "friends" => p.Author.IsFriend, "removed" => !p.Author.IsFriend, "close" => p.Close, "muted" => p.Muted, "always" => p.Always, _ => true }).ToArray();
        page = Math.Clamp(page, 1, Math.Max(1, (visiblePeople.Length + 39) / 40));
        var states = await db.PlatformStates.AsNoTracking().ToArrayAsync(ct); var runs = await db.Runs.AsNoTracking().OrderByDescending(r => r.Id).ToArrayAsync(ct);
        var requests = await db.RunRequests.AsNoTracking().Where(r => r.Status == "pending" || r.Status == "claimed").ToArrayAsync(ct);
        var postCounts = await db.Posts.GroupBy(p => p.Platform).Select(g => new { Platform = g.Key, Total = g.Count(), Hidden = g.Count(p => p.Hidden) }).ToArrayAsync(ct);
        var kv = await db.Kv.AsNoTracking().Where(k => k.Key.StartsWith("slot:")).ToDictionaryAsync(k => k.Key, k => k.Value, ct);
        var overviews = Platforms.All.Select(p => new PlatformOverview(p, authors.Count(a => a.Platform == p && a.IsFriend), postCounts.FirstOrDefault(c => c.Platform == p)?.Total ?? 0, postCounts.FirstOrDefault(c => c.Platform == p)?.Hidden ?? 0, states.FirstOrDefault(x => x.Platform == p), runs.FirstOrDefault(r => r.Platform == p && r.Kind == "collect"), runs.FirstOrDefault(r => r.Platform == p && r.Kind == "friends"), runs.Where(r => r.Platform == p && r.Status == "running").ToArray(), requests.Where(r => r.Platform == p).ToArray(), NextRun(s.Config, p, Clock.Now, kv))).ToArray();
        return new(docs, s, overviews, visiblePeople.Skip((page - 1) * 40).Take(40).ToArray(), visiblePeople.Length, page, query, platform, filter, section, await db.Get("disk:summary", ct) ?? "Not measured yet", await db.Get("disk:at", ct), await db.Posts.CountAsync(p => !p.Hidden && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision), ct), await db.Posts.CountAsync(p => p.LlmError != null || p.SummaryError != null, ct)) {
            Recovery = await RecoveryQuery.Read(db, s, ct: ct),
            Collections = runs.Where(r => r.Kind == "collect").Take(10).Concat(runs.Where(r => r.Id == collectionId)).DistinctBy(r => r.Id).ToArray(),
            RecentCompletions = section == "recovery" ? (await db.Posts.AsNoTracking().Where(p => p.JudgedAt > Clock.Now.AddDays(-1) && p.VerdictContentRevision == p.ContentRevision && p.LlmError == null).OrderByDescending(p => p.JudgedAt).Take(10).Select(p => new ProcessingCompletion(p.Id, p.Platform, "Classification completed", p.JudgedAt!.Value)).ToArrayAsync(ct)) : [],
            CollectionVisits = section == "recovery" ? await db.TimelineVisits.AsNoTracking().Where(v => db.Runs.Where(r => r.Kind == "collect").OrderByDescending(r => r.Id).Take(10).Select(r => r.Id).Contains(v.RunId) || v.RunId == collectionId).ToArrayAsync(ct) : []
        };
    }
    public static string NextRun(FeedConfig c, string platform, DateTime now, IReadOnlyDictionary<string, string> fired)
    {
        if (!c.Scheduler.Enabled) return "Scheduling paused";
        if (!c.Enabled(platform)) return "Platform paused";
        var local = TimeZoneInfo.ConvertTimeFromUtc(now, c.Zone); var settings = c.Platform(platform);
        if (settings.Schedule.Values.All(times => times.IsEmpty)) return "No scheduled collections";
        for (int day = 0; day < 3660; day++)
        {
            var date = local.Date.AddDays(day); var key = date.ToString("yyyy-MM-dd");
            var candidates = settings.Schedule.Where(x => settings.ScheduledOn(x.Key, DateOnly.FromDateTime(date))).SelectMany(x => x.Value.Select(slot => new { Mode = x.Key, Slot = slot, Fire = date + TimeSpan.Parse(slot) + TimeSpan.FromMinutes(Scheduler.Jitter(platform, x.Key, slot, key, c.Scheduler.JitterMinutes)) })).Where(x => x.Fire >= local.AddMinutes(-30) && !(day == 0 && fired.GetValueOrDefault($"slot:{platform}:{x.Mode}:{x.Slot}") == key)).OrderBy(x => x.Fire).ToArray();
            if (candidates.FirstOrDefault() is { } next)
            {
                var utc = new DateTimeOffset(DateTime.SpecifyKind(next.Fire, DateTimeKind.Unspecified), c.Zone.GetUtcOffset(next.Fire)).UtcDateTime;
                return $"{ModeName(next.Mode)} · {next.Fire:ddd dd MMM yyyy HH:mm} ({c.Zone.Id}) · {RelativeTime(utc, now)}";
            }
        }
        return "No run planned in the next ten years";
    }
    public async Task<string> Queue(string platform, string kind, string? mode, CancellationToken ct)
    {
        var s = documents.Read().Snapshot();
        if (!Platforms.All.Contains(platform) || kind is not ("collect" or "friends" or "login")) throw new FormatException("Unknown platform action.");
        if (!s.Config.Scheduler.Enabled) throw new FormatException("Scheduling is paused. Resume it before requesting work.");
        if (!s.Config.Enabled(platform) && kind != "login") throw new FormatException("Enable this platform before requesting work.");
        await using var db = factory.Open();
        if (kind != "login" && await db.PlatformStates.AnyAsync(p => p.Platform == platform && p.NeedsRelogin, ct)) throw new FormatException("Log in to this platform before collecting or refreshing people.");
        if (kind == "collect" && !Platforms.Modes.Contains(mode)) throw new FormatException("Unknown collection mode.");
        if (kind != "collect") mode = null;
        if (await db.Runs.AnyAsync(r => r.Platform == platform && r.Kind == kind && r.Status == "running", ct)) return "This action is already running.";
        var added = await Actions.EnsureRequest(db, kind, platform, mode, ct);
        return added == 0 ? "This action is already queued." : "Action queued. It will start when the platform browser is available.";
    }
    public async Task<string> QueueProcessing(CancellationToken ct)
    {
        var s = documents.Read().Snapshot();
        if (!s.Config.Scheduler.Enabled) throw new FormatException("Scheduling is paused. Resume it before requesting work.");
        await using var db = factory.Open();
        if (await db.Runs.AnyAsync(r => r.Kind == "process" && r.Status == "running", ct)) return "Processing is already running.";
        await Actions.EnsureRequest(db, "process", null, ct: ct);
        return "Processing queued. Only work due now is eligible; retry delays and limits still apply.";
    }
    static JsonArray Array(IEnumerable<string> values) => new(values.Select(x => JsonValue.Create(x)).ToArray());
    static int Number(IFormCollection f, string key, int minimum = 0) => int.TryParse(f[key], out var n) && n >= minimum ? n : throw new FormatException($"{key.Replace('_', ' ')} must be a whole number of at least {minimum}.");
    static bool Checked(IFormCollection f, string key) => f[key] == "true";
    static JsonObject PlatformObject(JsonObject root, string platform)
    {
        var platforms = ManagementFiles.Object(root, "platforms");
        if (!platforms.Any(x => x.Key.Equals(platform, StringComparison.OrdinalIgnoreCase))) platforms[platform] = new JsonObject { ["enabled"] = false };
        return ManagementFiles.Object(platforms, platform);
    }
    public async Task<string> SaveSettings(string action, IFormCollection form, CancellationToken ct)
    {
        var version = form["version"].ToString(); bool refilter = action is "rules" or "filters" or "category";
        using var processing = refilter ? ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("Processing is active. Try saving again after it finishes.") : null;
        using var ingestFb = refilter ? ResourceLock.Try(paths, "ingest-facebook") ?? throw new ResourceBusyException("Facebook import is active. Try again shortly.") : null;
        using var ingestIg = refilter ? ResourceLock.Try(paths, "ingest-instagram") ?? throw new ResourceBusyException("Instagram import is active. Try again shortly.") : null;
        if (refilter) { await using var pending = factory.Open(); await pending.Put("management:refilter_pending", "1", ct); }
        var saved = documents.Save(version, d => {
            var s = d.Snapshot(); var root = d.ConfigObject();
            if (action == "scheduler") { ManagementFiles.Set(ManagementFiles.Object(root, "scheduler"), "enabled", JsonValue.Create(Checked(form, "enabled"))); }
            else if (action == "platform") {
                var platform = form["platform"].ToString(); if (!Platforms.All.Contains(platform)) throw new FormatException("Unknown platform.");
                var p = PlatformObject(root, platform); ManagementFiles.Set(p, "enabled", JsonValue.Create(Checked(form, "enabled"))); ManagementFiles.Set(p, "likeback", JsonValue.Create(Checked(form, "likeback")));
            }
            else if (action == "schedule") {
                var platform = form["platform"].ToString(); var mode = form["mode"].ToString();
                if (!Platforms.All.Contains(platform) || !Platforms.Modes.Contains(mode)) throw new FormatException("Unknown platform or collection mode.");
                var p = PlatformObject(root, platform);
                var times = form["times"].ToString().Split(new[] { ',', ' ', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().Order().ToArray();
                ManagementFiles.SetMode(ManagementFiles.Object(p, "schedule"), mode, Array(times));
                ManagementFiles.SetMode(ManagementFiles.Object(p, "schedule_days"), mode, Array(form["days"].Select(x => x!)));
                var days = Number(form, "interval", 1); var start = form["start"].ToString();
                ManagementFiles.SetMode(ManagementFiles.Object(p, "schedule_intervals"), mode, days == 1 && start.Length == 0 ? null : new JsonObject { ["days"] = days, ["start_date"] = start });
            }
            else if (action == "timing") {
                var zone = form["timezone"].ToString(); try { TimeZoneInfo.FindSystemTimeZoneById(zone); } catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { throw new FormatException("Choose a valid timezone, such as Europe/Prague or UTC."); }
                ManagementFiles.Set(root, "timezone", JsonValue.Create(zone)); var scheduler = ManagementFiles.Object(root, "scheduler");
                foreach (var key in new[] { "jitter_minutes", "manual_cooldown_minutes" }) ManagementFiles.Set(scheduler, key, JsonValue.Create(Number(form, key)));
            }
            else if (action == "rules") {
                var text = d.Preferences;
                foreach (var (field, heading, prefix) in new[] { ("keywords", "Never show — my rules", "keyword: "), ("policy", "Plain-English policy", ""), ("learned", "Learned from thumbs", ""), ("muted", "Muted people", "mute: "), ("always", "Always show", "show: ") }) text = ManagementFiles.PreferenceSection(text, heading, prefix, ManagementFiles.Lines(form[field].ToString()));
                return d with { Preferences = text };
            }
            else if (action == "filters") {
                var f = ManagementFiles.Object(root, "filters"); ManagementFiles.Set(f, "audience", JsonValue.Create(form["audience"].ToString())); ManagementFiles.Set(f, "friend_tag_exception", JsonValue.Create(form["friend_tag_exception"].ToString()));
                ManagementFiles.Set(f, "blocked_types", Array(form["blocked_types"].Select(x => x!))); ManagementFiles.Set(f, "always_show_bypasses", Array(form["bypasses"].Select(x => x!)));
            }
            else if (action == "category" || action == "view") {
                var taxonomy = d.TaxonomyObject(); var key = form["key"].ToString(); var collection = action == "category" ? "categories" : "views";
                var items = taxonomy[collection]?.AsArray() ?? throw new FormatException("No definitions found.");
                var entry = items.OfType<JsonObject>().SingleOrDefault(x => x["key"]?.GetValue<string>() == key) ?? throw new FormatException("Definition no longer exists.");
                entry["label"] = form["label"].ToString();
                if (action == "category") { entry["definition"] = form["definition"].ToString(); entry["close_friends_only"] = Array(form["close_friends_only"].Select(x => x!)); entry["default"] = Checked(form, "default"); }
                else {
                    var current = s.Taxonomy.Views.Single(v => v.Key == key);
                    if (current.Authors is not null) entry["authors"] = Array(ManagementFiles.Lines(form["authors"].ToString()));
                    if (current.Category is not null) entry["category"] = form["category"].ToString();
                    if (current.Union is not null) entry["union"] = Array(form["members"].Select(x => x!));
                    if (current.Rare is not null) entry["rare"] = new JsonObject { ["max_posts"] = Number(form, "max_posts", 1), ["window_days"] = Number(form, "window_days", 1) };
                }
                return d with { Taxonomy = ManagementFiles.Json(taxonomy) };
            }
            else if (action == "storage") {
                foreach (var key in new[] { "raw_retention_days", "hidden_media_retention_days", "hidden_video_retention_days", "video_retention_days", "max_video_mb" }) ManagementFiles.Set(root, key, JsonValue.Create(Number(form, key)));
            }
            else if (action == "processing") {
                var llm = ManagementFiles.Object(root, "llm"); ManagementFiles.Set(llm, "enabled", JsonValue.Create(Checked(form, "enabled"))); ManagementFiles.Set(llm, "vision", JsonValue.Create(Checked(form, "vision")));
                ManagementFiles.Set(llm, "summary_languages", Array(ManagementFiles.Lines(form["summary_languages"].ToString())));
                foreach (var key in new[] { "threshold", "parallel", "timeout_seconds", "max_tokens", "summary_max_tokens" }) ManagementFiles.Set(llm, key, JsonValue.Create(Number(form, key)));
            }
            else throw new FormatException("Unknown settings action.");
            return d with { Config = ManagementFiles.Json(root) };
        });
        if (refilter) await Refilter(saved.Snapshot(), CancellationToken.None);
        return refilter ? "Saved. Existing posts were refiltered; model judgments are not rerun automatically." : "Saved. New runs use these settings; work already running keeps its starting settings.";
    }
    public async Task<string> Person(long id, string action, bool enable, string version, CancellationToken ct)
    {
        using var processing = ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("Processing is active. Try again after it finishes.");
        using var ingestFb = ResourceLock.Try(paths, "ingest-facebook") ?? throw new ResourceBusyException("Facebook import is active. Try again shortly.");
        using var ingestIg = ResourceLock.Try(paths, "ingest-instagram") ?? throw new ResourceBusyException("Instagram import is active. Try again shortly.");
        await using var db = factory.Open(); var authors = await db.Authors.ToArrayAsync(ct); var person = authors.SingleOrDefault(a => a.Id == id) ?? throw new FormatException("Person no longer exists.");
        if (action == "remove" && !person.IsFriend) throw new FormatException("Person is already outside the feed list.");
        if (action == "close" && enable && !person.IsFriend) throw new FormatException("Only imported friends/followed people can be close friends.");
        var key = Reference(person);
        string[] Without(IEnumerable<string> entries, IEnumerable<Author> candidates, bool mute = false) => entries.SelectMany(entry => {
            var matched = (mute ? candidates.Where(a => Identity.Matches(entry, a)) : Identity.Resolve(entry, candidates)).ToArray();
            return matched.Any(a => a.Id == id) ? matched.Where(a => a.Id != id).Select(Reference) : new[] { entry };
        }).Distinct().ToArray();
        await db.Put("management:refilter_pending", "1", ct);
        var saved = documents.Save(version, d => {
            var s = d.Snapshot();
            if (action is "close" or "remove") {
                var root = d.ConfigObject(); var p = PlatformObject(root, person.Platform); var candidates = authors.Where(a => a.Platform == person.Platform && a.IsFriend);
                var close = Without(s.Config.Platform(person.Platform).CloseFriends, candidates).ToList(); if (action == "close" && enable) close.Add(key);
                ManagementFiles.Set(p, "close_friends", Array(close));
                if (action == "remove") ManagementFiles.Set(p, "home_timeline_authors", Array(Without(s.Config.Platform(person.Platform).HomeTimelineAuthors, candidates)));
                return d with { Config = ManagementFiles.Json(root) };
            }
            if (action is not ("mute" or "always")) throw new FormatException("Unknown person action.");
            var entries = Without(action == "mute" ? s.Preferences.Mutes : s.Preferences.Shows, authors, action == "mute").ToList(); if (enable) entries.Add(key);
            return d with { Preferences = ManagementFiles.PreferenceSection(d.Preferences, action == "mute" ? "Muted people" : "Always show", action == "mute" ? "mute: " : "show: ", entries) };
        });
        if (action == "remove") {
            person.IsFriend = false;
            await db.SweepTargets.Where(t => t.AuthorId == id && (t.State == "pending" || t.State == "retry")).ExecuteUpdateAsync(u => u.SetProperty(t => t.State, "skipped"), ct);
            await db.SaveChangesAsync(ct);
        }
        await Refilter(saved.Snapshot(), CancellationToken.None);
        return action switch {
            "remove" => "Removed from the local feed list. Stored posts are kept and audience rules reapplied. A later import may add this person back.",
            "close" => "Close-friend setting saved. Existing posts were refiltered. Added close friends may need an explicit rescore for previously excluded categories.",
            "mute" => enable ? "Muted. Existing posts were refiltered." : "Unmuted. Existing posts were refiltered; other visibility rules still apply.",
            _ => "Always-show setting saved. Existing posts were refiltered; mute and category rules still apply."
        };
    }
    public async Task ResumeRefilter(CancellationToken ct)
    {
        await using var db = factory.Open(); if (await db.Get("management:refilter_pending", ct) is null) return;
        using var processing = ResourceLock.Try(paths, "processing"); if (processing is null) return;
        using var fb = ResourceLock.Try(paths, "ingest-facebook"); if (fb is null) return;
        using var ig = ResourceLock.Try(paths, "ingest-instagram"); if (ig is null) return;
        await Refilter(documents.Read().Snapshot(), ct);
    }
    async Task Refilter(InstanceSnapshot snapshot, CancellationToken ct)
    {
        await using var db = factory.Open(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var authors = await db.Authors.AsNoTracking().ToArrayAsync(ct); long last = 0;
        while (true) {
            var posts = await db.Posts.Where(p => p.Id > last).OrderBy(p => p.Id).Take(500).ToArrayAsync(ct); if (posts.Length == 0) break;
            foreach (var p in posts) {
                var rules = new RuleContext(authors.FirstOrDefault(a => a.Id == p.AuthorId), authors, snapshot); p.VisibilityRevision++;
                Filters.Apply(p, rules);
                if (!p.Hidden && p.Judged && p.LlmScore < snapshot.Config.Llm.Threshold && !rules.Shield("llm")) p.SetHidden("llm", $"llm {p.LlmScore}: {p.LlmReason}");
                if (p.CategoriesJson is not null) p.CategoriesJson = JsonSerializer.Serialize(Filters.Restrict(JsonSerializer.Deserialize<string[]>(p.CategoriesJson) ?? [], rules, p.Platform));
            }
            last = posts[^1].Id; await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        }
        await db.Kv.Where(k => k.Key == "management:refilter_pending").ExecuteDeleteAsync(ct);
        await transaction.CommitAsync(ct);
    }
}
