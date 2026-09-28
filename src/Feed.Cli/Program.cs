using System.Runtime.InteropServices;
using System.Text.Json;
using Feed.Cli;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

return await CliProgram.Run(args);
namespace Feed.Cli
{
public static class CliProgram
{
    public const string Usage = """
Feed v3 — private chronological feed
Usage: dotnet out/Feed.Cli.dll COMMAND [--data DIR] [options]
  init | rules | status | help
  login --platform facebook|instagram
  collect --platform X|all [--mode home|close_friends|all_followed] [--scrolls N] [--person H] [--friends A,B] [--friends-limit N] [--retry-incomplete]
  friends --platform X [--offline [--all-dirs]]
  reparse --platform X [--run DIR | --path DIR | --all | --failed] [--no-network]
  process --platform X|all [--limit N]
  refilter [--post-id N | --since DATE | --all]
  rescore [--all] [--platform X] [--limit N] [--since DATE] [--post-id N] [--author REF] [--text-only]
  summarize [--limit N]
  like [--platform X] [--post-id N]
  avatars --platform X
  feedback [--limit N] [--uncurated]
  curate [--write LINE --cites N,N [--approved] [--author REF] | --dismiss N,N]
  raw prune [--before DATE] [--dry-run] | media prune [--dry-run]
  browser install|install-deps
  profile import --platform X --from DIR
Defaults: reparse newest feed capture; refilter since latest collect; explicit model work newest first; process oldest due work, fair platform/task turns. --limit caps selected scope per stage, not successes. Every operation prints scope and result. Personal configuration lives only in the selected data directory.
Exit: 0 success, 1 operation failed, 2 configuration/arguments, 75 deferred, 130 cancelled.
""";
    public static async Task<int> Run(string[] args)
    {
        var originalOutput = Console.Out;
        var a = new Arguments(args); Run? run = null; RunLedger? ledger = null; LogSink? logs = null;
        using var cancel = new CancellationTokenSource(); ConsoleCancelEventHandler interrupt = (_, e) => { e.Cancel = true; cancel.Cancel(); }; Console.CancelKeyPress += interrupt;
        using var terminate = !OperatingSystem.IsWindows() ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancel.Cancel(); }) : null;
        var ct = cancel.Token;
        try
        {
            if (a.Command == "browser") { if (a.Positionals.Count != 1 || a.Positionals[0] is not ("install" or "install-deps")) throw new ArgumentException("browser install|install-deps"); return Microsoft.Playwright.Program.Main([a.Positionals[0], "chromium"]); }
            var paths = new InstancePaths(a.Get("data")); paths.Create(); InstanceFiles files;
            try { files = new(paths); } catch (Exception e) when (e is FormatException or JsonException) { Console.Error.WriteLine("config error: " + e.Message); return 2; }
            var s = files.Current; var factory = new DbFactory(paths); await using (var db = factory.Open()) await db.Initialize(ct);
            var level = (a.Get("log-level") ?? s.Config.Ui.LogLevel) switch { "trace" => LogLevel.Trace, "debug" => LogLevel.Debug, "warn" => LogLevel.Warning, "error" => LogLevel.Error, _ => LogLevel.Information };
            logs = new(paths, "cli", level, a.Flag("quiet")); Console.SetOut(new LoggedOutput(originalOutput, logs, a.Flag("quiet"))); logs.Write(LogLevel.Information, "cli", "command " + a.Command + "; instance=" + paths.Root); ledger = new(factory);
            if (a.Command is "help" or "--help" || a.Flag("help")) { Console.WriteLine(Usage); return 0; }
            if (a.Get("request-id") is null != (a.Get("claim-token") is null)) throw new ArgumentException("--request-id and --claim-token are required together");
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; var media = new MediaFiles(paths, http); var ingest = new Ingest(paths, factory, media); var capture = new Capture(paths, factory, ingest, ledger); var actions = new Actions(factory); var maintenance = new Maintenance(paths, factory, http);
            if (a.Command == "init") { Console.WriteLine($"init: instance at {paths.Root}, database {paths.Database} ready; config {(File.Exists(paths.Get("config.json")) ? "found" : "absent (defaults: every platform paused)")}"); return 0; }
            if (a.Command == "rules") { Console.Write(await Reports.Rules(factory, s)); return 0; }
            if (a.Command == "status") { foreach (var line in await ledger.Recover(s.Config, ct)) Console.WriteLine(line); Console.Write(await Reports.Status(factory, s)); return 0; }
            if (a.Command is "feedback" or "curate")
            {
                await using var db = factory.Open(); var rows = await db.Feedback.OrderByDescending(f => f.Id).Where(f => !(a.Flag("uncurated") || a.Command == "curate") || !f.Curated).Take(a.Number("limit") ?? (a.Command == "curate" ? int.MaxValue : 50)).ToListAsync(ct);
                var citeText = a.Get("cites") ?? a.Get("dismiss");
                if (a.Get("write") is { } line)
                {
                    if (citeText is null) throw new ArgumentException("--cites is required"); if (System.Text.RegularExpressions.Regex.IsMatch(line, @"\b(mute|never show|block|ban)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && !a.Flag("approved")) throw new ArgumentException("mute-like curation requires --approved");
                    var path = paths.Get("preferences.md"); var text = File.Exists(path) ? await File.ReadAllTextAsync(path, ct) : ""; var ids = ParseIds(citeText); if (!ids.All(id => rows.Any(f => f.Id == id))) throw new ArgumentException("unknown or already curated cited signal");
                    const string heading = "## Learned from thumbs feedback"; if (!text.Contains(heading, StringComparison.OrdinalIgnoreCase)) text += "\n\n" + heading + "\n";
                    var index = text.IndexOf(heading, StringComparison.OrdinalIgnoreCase); var next = text.IndexOf("\n## ", index + heading.Length, StringComparison.Ordinal); if (next < 0) next = text.Length;
                    text = text.Insert(next, $"\n- {Clock.Now:yyyy-MM-dd}: {line.Trim().TrimEnd('.')}. (thumbs {string.Join(", ", ids.Select(id => "#" + id))})\n"); InstancePaths.AtomicWrite(path, text);
                }
                if (citeText is not null) { var ids = ParseIds(citeText); if (!ids.All(id => rows.Any(f => f.Id == id))) throw new ArgumentException("unknown cited signal"); await db.Feedback.Where(f => ids.Contains(f.Id)).ExecuteUpdateAsync(u => u.SetProperty(f => f.Curated, true), ct); Console.WriteLine($"curate: marked {ids.Length} cited rows; no visibility changed"); if (a.Get("author") is not null && a.Get("write") is not null) return await Run(["rescore", "--data", paths.Root, "--author", a.Get("author")!]); }
                else foreach (var f in rows) Console.WriteLine($"thumb #{f.Id} post=#{f.PostId} author=#{f.AuthorId} value={f.Value} view={f.ViewKey} labels={f.CategoriesAtVote} reason={f.ReasonAtVote}");
                return 0;
            }
            if (a.Command is "raw" or "media") { if (a.Positionals.FirstOrDefault() != "prune") throw new ArgumentException("prune is required"); Console.WriteLine(await maintenance.Prune(s.Config, a.Command == "raw", a.Flag("dry-run"), a.Date("before"), ct)); return 0; }
            if (a.Command == "profile") { if (a.Positionals.FirstOrDefault() != "import" || a.Get("from") is not { } from || !Directory.Exists(from)) throw new ArgumentException("profile import --platform X --from <directory>"); var p = a.Platform(); if (Directory.EnumerateFileSystemEntries(from, "Singleton*").Any()) throw new ArgumentException("Close the source Chromium browser and remove only verified stale Singleton locks before importing"); using var held = ResourceLock.Try(paths, p) ?? throw new ResourceBusyException("platform busy"); BrowserSession.CopyProfile(from, paths.Get("profiles", p)); Console.WriteLine($"profile import {p}: copied profile with caches excluded"); return 0; }
            if (!ClosedValues.RunKinds.Contains(a.Command) && a.Command != "avatars") { Console.Error.WriteLine("unknown command: " + a.Command); return 1; }
            var platform = a.Command is "collect" or "process" ? a.Platform(true) : a.Command is "login" or "friends" or "reparse" or "avatars" ? a.Platform() : a.Platform(true, "all");
            var platforms = platform == "all" ? (a.Command == "like" ? Platforms.All : Platforms.All.Where(s.Config.Enabled).ToArray()) : [platform];
            var mode = Platforms.Mode(a.Get("mode") ?? "home"); if (!Platforms.Modes.Contains(mode)) throw new ArgumentException("unsupported collect mode: " + mode);
            if (a.Flag("retry-incomplete") && (a.Command != "collect" || mode != "all_followed" || a.Get("person") is not null || a.Get("friends") is not null)) throw new ArgumentException("--retry-incomplete requires collect --mode all_followed without --person/--friends");
            var limit = a.Number("limit"); var postId = a.Number("post-id"); var scrolls = a.Number("scrolls"); var friendsLimit = a.Number("friends-limit"); var since = a.Date("since");
            if (a.Command == "like" && postId is { } likePost)
            {
                await using var lookup = factory.Open(); var post = await lookup.Posts.FindAsync([likePost], ct) ?? throw new ArgumentException("unknown post");
                if (platform != "all" && platform != post.Platform) throw new ArgumentException("post belongs to a different platform");
                platforms = [post.Platform];
            }
            if (a.Command is "rescore" or "summarize" && !s.Config.Llm.Enabled) throw new ArgumentException("model is disabled");
            if (a.Command is "collect" or "friends" or "login" or "like")
            {
                int aggregate = 0;
                foreach (var p in platforms)
                {
                    run = await ledger.Start(a.Command, p, a.Command == "collect" ? mode : null, a.Get("trigger") ?? "manual", a.Number("request-id"), a.Get("claim-token"), ct); int exit = 0; string status = "ok";
                    if (a.Command == "login") { await ledger.Phase(run.Id, "browser", ct); await capture.Login(p, s, ct); }
                    else if (a.Command == "like") { if (postId is { } id) { var error = await actions.QueueLike(id, s, true, ct); if (error is not null) throw new ArgumentException(error); } exit = await new LikeSender(paths, factory, capture).Send(p, s, postId, ct); status = exit == 75 ? "refused" : exit == 1 ? "error" : "ok"; }
                    else if (a.Command == "friends" && a.Flag("offline")) { var dirs = RawDirs(paths, p, true, a.Flag("all-dirs")); if (dirs.Length == 0) throw new IOException("no friends capture directories"); using var held = ResourceLock.Try(paths, "ingest-" + p) ?? throw new ResourceBusyException("ingest busy"); foreach (var dir in dirs) foreach (var file in Directory.EnumerateFiles(dir).Where(IsRaw).Order()) { var result = await ingest.File(p, file, s, true, ct); run.PostsFound += result.Found; } Console.WriteLine($"friends {p}: ok (imported={run.PostsFound}, complete=false, pruned=0; offline add-only)"); }
                    else { status = await capture.Collect(p, s, run, new(mode, scrolls ?? 12, scrolls, a.Get("person"), a.Get("friends")?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries), friendsLimit, a.Flag("retry-incomplete")), a.Command == "friends", ct); exit = status is "error" or "checkpoint" ? 1 : 0; }
                    await ledger.Finish(run, status, exit, run.Error, a.Get("claim-token")); logs.Write(LogLevel.Information, a.Command, $"{p}: {status} run=#{run.Id} exit={exit}"); run = null; aggregate = Math.Max(aggregate, exit);
                }
                Console.WriteLine($"{a.Command}: finished platform={platform}, exit={aggregate}"); return aggregate;
            }
            run = await ledger.Start(a.Command == "avatars" ? "friends" : a.Command, platform == "all" ? null : platform, trigger: a.Get("trigger") ?? "manual", requestId: a.Number("request-id"), token: a.Get("claim-token"), ct: ct);
            await ledger.Phase(run.Id, "processing", ct); int resultCode = 0;
            if (a.Command is "process" or "rescore" or "summarize")
            {
                Console.WriteLine($"scope: {a.Command} platform={platform}, order={(a.Command == "process" ? "oldest due per stage with rotating turns" : "newest post first")}, selected limit={limit?.ToString() ?? "all eligible"}, batch={s.Config.Llm.Batch}, parallel={s.Config.Llm.Parallel}");
                var counts = await new Processing(paths, factory, new(http), media, ingest).Run(s, new(a.Command, platform, limit, a.Flag("all"), postId, since, a.Get("author"), a.Flag("text-only")), line => { Console.WriteLine(line); }, ct, async counts => { if (run is null) return; var json = JsonSerializer.Serialize(counts, new JsonSerializerOptions { IncludeFields = true }); run.StatsJson = json; await using var progressDb = factory.Open(); await progressDb.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.StatsJson, json)); });
                run.StatsJson = JsonSerializer.Serialize(counts, new JsonSerializerOptions { IncludeFields = true });
                foreach (var stage in counts.Stages.OrderBy(x => x.Key)) Console.WriteLine($"process {stage.Key}: selected={stage.Value.Selected}, completed={stage.Value.Completed}, failed={stage.Value.Failed}, superseded={stage.Value.Superseded}, selected-but-not-dispatched={stage.Value.Undispatched}, remaining={stage.Value.Remaining}");
                Console.WriteLine($"{a.Command} {platform}: {counts}"); resultCode = counts.Failed > 0 ? 1 : 0;
            }
            else if (a.Command == "reparse")
            {
                using var held = ResourceLock.Try(paths, "ingest-" + platform) ?? throw new ResourceBusyException("ingest busy");
                if (a.Flag("failed"))
                {
                    if (a.Flag("all") || a.Get("path") is not null || a.Get("run") is not null) throw new ArgumentException("--failed cannot be combined with --all, --path or --run");
                    await using var db = factory.Open();
                    var raws = await db.RawSnapshots.AsNoTracking().Where(r => r.Platform == platform && !r.Parsed && !r.Deleted && r.Error != null && !db.Runs.Any(x => x.Id == r.RunId && x.Status == "running" && x.Phase == "browser")).OrderBy(r => r.Id).ToArrayAsync(ct);
                    Console.WriteLine($"scope: reparse {raws.Length} failed snapshots on {platform}, raw id ascending; no model calls");
                    foreach (var raw in raws) { var r = await ingest.File(platform, paths.Get(raw.Path), s, a.Flag("no-network"), ct); run.PostsFound += r.Found; run.PostsNew += r.New; resultCode |= r.Failed > 0 ? 1 : 0; }
                    Console.WriteLine($"reparse {platform}: {(resultCode == 0 ? "ok" : "error")} (failed snapshots selected={raws.Length}, found={run.PostsFound}, new={run.PostsNew})");
                }
                else
                {
                var dirs = a.Get("path") is { } foreign ? new[] { Path.GetFullPath(foreign) } : a.Get("run") is { } name ? [paths.Get("raw", platform, Identity.Safe(name))] : RawDirs(paths, platform, false, a.Flag("all")); if (dirs.Length == 0) throw new IOException("no feed capture directories");
                if (a.Get("path") is not null)
                {
                    if (!Directory.Exists(dirs[0])) throw new ArgumentException("missing directory: " + dirs[0]);
                    var imported = paths.Get("raw", platform, "imported-" + Clock.Now.ToString("yyyyMMddTHHmmss") + "-" + run.Id); Directory.CreateDirectory(imported);
                    foreach (var file in Directory.EnumerateFiles(dirs[0]).Where(IsRaw).Order()) { var destination = Path.Combine(imported, Path.GetFileName(file)); File.Copy(file, destination, false); await ingest.Register(platform, destination, run.Id, ct); }
                    dirs = [imported]; run.RawDir = Path.GetFileName(imported);
                }
                Console.WriteLine($"scope: reparse {dirs.Length} directories, filename ascending, all observations; no model calls");
                foreach (var dir in dirs) { if (!Directory.Exists(dir)) throw new ArgumentException("missing directory: " + dir); foreach (var file in Directory.EnumerateFiles(dir).Where(IsRaw).Order()) { var r = await ingest.File(platform, file, s, a.Flag("no-network"), ct); run.PostsFound += r.Found; run.PostsNew += r.New; resultCode |= r.Failed > 0 ? 1 : 0; } }
                Console.WriteLine($"reparse {platform}: {(resultCode == 0 ? "ok" : "error")} (dirs={dirs.Length}, found={run.PostsFound}, new={run.PostsNew})");
                }
            }
            else if (a.Command == "refilter")
            {
                int total = 0, hidden = 0, shown = 0; await using var db = factory.Open(); if (!a.Flag("all") && since is null && postId is null) since = await db.Runs.Where(r => r.Kind == "collect").OrderByDescending(r => r.StartedAt).Select(r => (DateTime?)r.StartedAt).FirstOrDefaultAsync(ct) ?? Clock.Now;
                foreach (var p in platforms) { using var held = ResourceLock.Try(paths, "ingest-" + p) ?? throw new ResourceBusyException("ingest busy"); var authors = await db.Authors.ToListAsync(ct); var posts = await db.Posts.Where(x => x.Platform == p && (postId == null || x.Id == postId) && (since == null || x.CapturedAt >= since)).OrderByDescending(x => x.PostedAt).ThenByDescending(x => x.Id).ToListAsync(ct); foreach (var post in posts) { total++; bool was = post.Hidden; post.VisibilityRevision++; var rules = new RuleContext(authors.FirstOrDefault(x => x.Id == post.AuthorId), authors, s); Filters.Apply(post, rules); if (post.CategoriesJson is not null) post.CategoriesJson = JsonSerializer.Serialize(Filters.Restrict(JsonSerializer.Deserialize<string[]>(post.CategoriesJson) ?? [], rules, p)); if (!was && post.Hidden) hidden++; if (was && !post.Hidden) shown++; } await db.SaveChangesAsync(ct); }
                Console.WriteLine($"refilter: total={total}, hidden={hidden}, shown={shown}, unchanged={total - hidden - shown}; scope={(a.Flag("all") ? "all history" : $"since={since:O} post={postId}")}, newest first");
            }
            else if (a.Command == "avatars")
            {
                using var held = ResourceLock.Try(paths, "ingest-" + platform) ?? throw new ResourceBusyException("ingest busy"); int downloaded = 0;
                foreach (var dir in RawDirs(paths, platform, true, true)) foreach (var file in Directory.EnumerateFiles(dir).Where(IsRaw).Order())
                {
                    var people = PayloadParser.Parse(platform, await File.ReadAllTextAsync(file, ct), true).People;
                    foreach (var person in people.Where(p => p.Avatar is not null))
                    {
                        await using var db = factory.Open(); var refs = Identity.Refs(platform, person.Id, person.Url);
                        var id = await db.AuthorKeys.Where(k => refs.Contains(k.Key)).Select(k => (long?)k.AuthorId).FirstOrDefaultAsync(ct); if (id is null) continue;
                        var author = await db.Authors.FindAsync([id], ct); if (author is null || author.AvatarPath is not null) continue;
                        var image = await media.Image($"media/avatars/{platform}/{Identity.Safe(person.Id ?? id.ToString()!)}{MediaFiles.Extension(person.Avatar!)}", person.Avatar!, false, true, ct);
                        if (image.Path is not null) { author.AvatarPath = image.Path; await db.SaveChangesAsync(ct); downloaded++; }
                    }
                }
                Console.WriteLine($"avatars {platform}: backfilled={downloaded}; known authors missing avatars from all friends raws, newest directories first");
            }
            await ledger.Finish(run, resultCode == 0 ? a.Get("path") is not null ? "imported" : "ok" : "error", resultCode, token: a.Get("claim-token")); run = null; return resultCode;
        }
        catch (Exception e)
        {
            int code = e is OperationCanceledException ? 130 : e is ResourceBusyException ? 75 : e is ArgumentException or FormatException ? 2 : 1;
            var message = code == 130 ? "cancelled by operator" : $"{(code == 1 ? "fatal: " : "")}{e.GetType().Name}: {e.Message} | cause: {e.GetBaseException().Message}";
            Console.Error.WriteLine(message); if (code == 1) Console.Error.WriteLine(e.StackTrace); logs?.Write(LogLevel.Error, "cli", message);
            if (run is not null && ledger is not null) await ledger.Finish(run, code == 130 ? "cancelled" : code == 75 ? "refused" : "error", code, message, a.Get("claim-token")); return code;
        }
        finally { Console.Out.Flush(); Console.SetOut(originalOutput); Console.CancelKeyPress -= interrupt; logs?.Dispose(); }
    }
    static long[] ParseIds(string text) { try { return text.Split(',').Select(long.Parse).Distinct().ToArray(); } catch (FormatException) { throw new ArgumentException("citations must be comma-separated ids"); } }
    static bool IsRaw(string path) => Path.GetExtension(path) is ".json" or ".jsonl";
    static string[] RawDirs(InstancePaths paths, string platform, bool friends, bool all) { var root = paths.Get("raw", platform); return Directory.Exists(root) ? Directory.EnumerateDirectories(root).Where(d => Ingest.FriendsDirectory(d) == friends).OrderDescending().Take(all ? int.MaxValue : 1).ToArray() : []; }
}
sealed class LoggedOutput(TextWriter output, LogSink sink, bool quiet) : TextWriter
{
    readonly object gate = new(); readonly System.Text.StringBuilder pending = new();
    public override System.Text.Encoding Encoding => output.Encoding;
    public override void Write(char value) { lock (gate) { if (value == '\n') FlushLine(); else if (value != '\r') pending.Append(value); } }
    public override void Write(string? value) { if (value is not null) lock (gate) foreach (var character in value) Write(character); }
    public override void WriteLine(string? value) { lock (gate) { Write(value); FlushLine(); } }
    void FlushLine() { var line = pending.ToString(); pending.Clear(); sink.Write(LogLevel.Information, "cli", line, emitConsole:false); if (!quiet) output.WriteLine(line); }
    public override void Flush() { lock (gate) { if (pending.Length > 0) FlushLine(); output.Flush(); } }
}

}
