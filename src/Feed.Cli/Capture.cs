using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
namespace Feed.Cli;

public sealed record CaptureOptions(string Mode = "home", int Scrolls = 12, int? TimelineScrolls = null, string? Person = null, string[]? Friends = null, int? FriendsLimit = null, bool RetryIncomplete = false);
public sealed class Capture(InstancePaths paths, DbFactory factory, Ingest ingest, RunLedger ledger)
{
    public static Author[] HomeTimelines(string platform, PlatformConfig config, CaptureOptions options, IEnumerable<Author> authors)
    {
        if (options.Person is not null || options.Mode is not ("home" or "close_friends")) return [];
        var eligible = authors.Where(a => a.Platform == platform && a.IsFriend).ToArray();
        var entries = config.HomeTimelineAuthors.AsEnumerable();
        if (options.Mode == "close_friends") entries = entries.Concat(options.Friends ?? config.CloseFriends.ToArray());
        return entries.SelectMany(e => Identity.Resolve(e, eligible)).DistinctBy(a => a.Id).Where(a => a.Url is not null).ToArray();
    }
    public async Task Login(string platform, InstanceSnapshot s, CancellationToken ct)
    {
        await using var session = await BrowserSession.Open(paths, platform, s.Config, ct); var page = session.Page;
        Console.WriteLine($"Log in to {platform} through the noVNC browser, including two-factor authentication. Waiting up to 10 minutes.");
        await page.GotoAsync(Platforms.Home(platform), new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
        var end = DateTime.UtcNow.AddMinutes(10);
        while (DateTime.UtcNow < end)
        {
            ct.ThrowIfCancellationRequested();
            if (!await Site.Checkpoint(page, platform)) await Site.DismissNotificationPrompt(page);
            if (Uri.TryCreate(page.Url, UriKind.Absolute, out var uri) && (uri.AbsolutePath == "/" || platform == "facebook" && uri.AbsolutePath == "/home.php") && await page.Locator(Site.Selectors(platform, false)).CountAsync() > 0 && !await Site.Checkpoint(page, platform)) { await Relogin(platform, false, ct); session.ExportCookiesOnClose(); return; }
            await Task.Delay(2000, ct);
        }
        throw new TimeoutException("login timed out; inspect noVNC and selectors before retrying");
    }
    public async Task Relogin(string platform, bool value, CancellationToken ct) { await using var db = factory.Open(); var state = await db.PlatformStates.FindAsync([platform], ct); if (state is null) { state = new() { Platform = platform }; db.Add(state); } state.NeedsRelogin = value; state.UpdatedAt = Clock.Now; await db.SaveChangesAsync(ct); }
    public async Task<string> Collect(string platform, InstanceSnapshot s, Run run, CaptureOptions options, bool friends, CancellationToken ct)
    {
        await using (var db = factory.Open()) if (await db.PlatformStates.AnyAsync(p => p.Platform == platform && p.NeedsRelogin, ct)) return "refused";
        var prefix = friends ? "friends-" : options.Mode == "close_friends" ? "close-friends-" : options.Mode == "all_followed" ? "friends-timelines-" : "";
        run.RawDir = prefix + Clock.Now.ToString("yyyyMMddTHHmmss") + "-" + run.Id;
        var directory = paths.Get("raw", platform, run.RawDir); Directory.CreateDirectory(directory);
        await using (var db = factory.Open()) await db.Runs.Where(r => r.Id == run.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.RawDir, run.RawDir), ct);
        var pending = new ConcurrentBag<Task>(); var hashes = new HashSet<string>(); var captured = new ConcurrentDictionary<string, Observation>(); var surfaceObservations = new ConcurrentDictionary<string, Observation>(); var seen = new HashSet<string>(); var serial = new SemaphoreSlim(1); int batches = 0, listenerErrors = 0, scrolls = 0, visited = 0, incomplete = 0, emptyVisits = 0, unrendered = 0; bool explicitEmpty = false;
        await using (var db = factory.Open()) foreach (var id in await db.Posts.Where(p => p.Platform == platform).Select(p => p.PlatformPostId).ToListAsync(ct)) seen.Add(id);
        async Task Save(string body)
        {
            await serial.WaitAsync(ct);
            try
            {
                var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))); if ((platform == "instagram" || friends) && !hashes.Add(hash))
                {
                    // The bytes already have a raw ledger entry; the visit still observed them.
                    var replay = PayloadParser.Parse(platform, body, friends); explicitEmpty |= replay.ExplicitEmpty;
                    foreach (var observation in replay.Posts) surfaceObservations.AddOrUpdate(observation.Post.PlatformPostId, observation, (_, existing) => existing with { TimelineOwnerIds = existing.TimelineOwnerIds.Concat(observation.TimelineOwnerIds).Distinct().ToArray() });
                    return;
                }
                var path = Path.Combine(directory, $"{++batches:000}{(platform == "facebook" ? ".jsonl" : ".json")}"); InstancePaths.AtomicWrite(path, body); await ingest.Register(platform, path, run.Id, ct);
                var parsed = PayloadParser.Parse(platform, await File.ReadAllTextAsync(path, ct), friends); explicitEmpty |= parsed.ExplicitEmpty;
                foreach (var diagnostic in parsed.Diagnostics) { listenerErrors++; Console.Error.WriteLine("parser warning: " + diagnostic); }
                foreach (var observation in parsed.Posts) { captured.TryAdd(observation.Post.PlatformPostId, observation); surfaceObservations.AddOrUpdate(observation.Post.PlatformPostId, observation, (_, existing) => existing with { TimelineOwnerIds = existing.TimelineOwnerIds.Concat(observation.TimelineOwnerIds).Distinct().ToArray() }); }
            }
            catch (Exception e) when (e is not OperationCanceledException) { Interlocked.Increment(ref listenerErrors); Console.Error.WriteLine("capture warning: " + e.Message); }
            finally { serial.Release(); }
        }
        async Task Drain() { var tasks = pending.ToArray(); if (tasks.Length > 0) await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30), ct); }
        string status = "ok"; string? error = null;
        try
        {
            await ledger.Phase(run.Id, "browser", ct);
            await using var session = await BrowserSession.Open(paths, platform, s.Config, ct); var page = session.Page;
            await using (var db = factory.Open()) if (await db.PlatformStates.AnyAsync(p => p.Platform == platform && p.NeedsRelogin, ct)) return "refused";
            session.Context.Response += (_, response) =>
            {
                if (!Site.Gate(platform, response.Url, response.Request.Method, response.Status, response.Headers.GetValueOrDefault("content-type", ""), response.Request.PostData, friends)) return;
                async Task Receive() { try { await Save(await response.TextAsync()); } catch (Exception e) when (e is not OperationCanceledException) { Interlocked.Increment(ref listenerErrors); Console.Error.WriteLine("response warning: " + e.Message); } }
                pending.Add(Receive());
            };
            async Task Check() { ct.ThrowIfCancellationRequested(); if (await Site.Checkpoint(page, platform)) throw new CheckpointException(); await Site.DismissNotificationPrompt(page); }
            async Task<(string Status, string Stop)> Surface(string url, bool timeline, int cap)
            {
                await Drain(); surfaceObservations.Clear(); explicitEmpty = false; await page.GotoAsync(url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }); await Check();
                var end = DateTime.UtcNow.AddSeconds(60); bool rendered = false;
                while (DateTime.UtcNow < end) { await Check(); await Drain(); if (explicitEmpty && timeline) return ("empty", "explicit_empty"); if (await page.Locator(Site.Selectors(platform, timeline)).CountAsync() > 0) { rendered = true; break; } await Task.Delay(2000, ct); }
                if (!rendered) { await Check(); if (timeline) return ("unrendered", "timeout"); throw new IOException("feed never appeared; session stale or page blocked"); }
                if (platform == "facebook") { int n = 0; foreach (var embedded in PayloadParser.Embedded(await page.ContentAsync())) { await Save(embedded); n++; } Console.WriteLine($"embedded: {n} preloaded batch(es)"); }
                var personVisit = options.Person is not null && options.Mode != "all_followed";
                var progress = new CollectionProgress(personVisit);
                for (int n = 0; n < cap; n++)
                {
                    await Check();
                    var before = personVisit ? await ScrollPosition.Read(page) : null;
                    var observedBefore = surfaceObservations.Count;
                    await page.Mouse.WheelAsync(0, 900); await Task.Delay(Random.Shared.Next(2000, 5001), ct); scrolls++; await Drain();
                    await Check();
                    var fresh = captured.Keys.Count(seen.Add);
                    var pageAdvanced = before is not null && (await ScrollPosition.Read(page)).AdvancedFrom(before);
                    if (explicitEmpty && timeline) return ("empty", "explicit_empty");
                    if (progress.Observe(fresh > 0, surfaceObservations.Count > observedBefore, pageAdvanced) is { } stop) return ("rendered", stop);
                }
                return ("rendered", "depth");
            }
            async Task Visit(Author? author, string url, int cap, SweepTarget? target = null)
            {
                (string Status, string Stop) outcome;
                Exception? failure = null; var errorsBefore = listenerErrors;
                try { outcome = await Surface(url, true, cap); }
                catch (CheckpointException e) { outcome = ("checkpoint", "checkpoint"); failure = e; }
                catch (OperationCanceledException e) { outcome = ("interrupted", "interrupted"); failure = e; }
                catch (Exception e) { outcome = ("error", "error"); failure = e; }
                visited++;
                var observed = surfaceObservations.Values.Where(x => Site.TimelineAuthorMatches(platform, url, author, x)).Select(x => x.Post).ToArray();
                var visit = new TimelineVisit { Platform = platform, AuthorId = author?.Id, Url = url.TrimEnd('/'), Name = author?.DisplayName ?? "", RunId = run.Id, Status = outcome.Status, CaptureStatus = outcome.Status == "empty" ? "empty" : outcome.Status == "rendered" && observed.Length > 0 && listenerErrors == errorsBefore ? "captured" : "incomplete", PostsFound = observed.Length, NewestPostedAt = observed.Select(p => p.PostedAt).Max(), OldestPostedAt = observed.Select(p => p.PostedAt).Min(), StopReason = outcome.Stop, Note = failure?.Message ?? (listenerErrors > errorsBefore ? $"{listenerErrors - errorsBefore} capture/parser error(s); inspect raw diagnostics for run #{run.Id}" : observed.Length == 0 && outcome.Status == "rendered" ? "Page rendered, but no post payload matched the target author; not evidence of an empty timeline" : null) };
                if (outcome.Status == "unrendered")
                {
                    try {
                    var text = await page.Locator("body").InnerTextAsync(); visit.Note = (await page.TitleAsync()) + " | " + page.Url + " | " + text[..Math.Min(200, text.Length)];
                    var name = "timeline-" + Identity.Safe(author?.PlatformAuthorId ?? new Uri(url).AbsolutePath.Trim('/')) + ".png"; await page.ScreenshotAsync(new() { Path = Path.Combine(directory, name) }); visit.Screenshot = $"media/diagnostics/{platform}/{run.RawDir}/{name}"; Directory.CreateDirectory(Path.GetDirectoryName(paths.Get(visit.Screenshot))!); File.Copy(Path.Combine(directory, name), paths.Get(visit.Screenshot), true);
                    } catch (Exception e) { visit.Note = "diagnostic failed: " + e.Message; }
                }
                if (visit.CaptureStatus == "incomplete") incomplete++; if (visit.CaptureStatus == "empty") emptyVisits++; if (visit.Status == "unrendered") unrendered++;
                await new TimelineCoverage(factory).Complete(visit, target, CancellationToken.None);
                if (failure is CheckpointException or OperationCanceledException) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                if (failure is not null) { status = "error"; error ??= failure.Message; }
                await Task.Delay(Random.Shared.Next(3000, 8001), ct);
            }
            if (friends)
            {
                ILocator? peopleDialog = null;
                if (platform == "facebook") await page.GotoAsync(Platforms.Home(platform) + "me/friends", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 });
                else
                {
                    var ownerUrl = s.Config.Platform(platform).SelfUsername is { Length: > 0 } handle ? Site.Timeline(platform, handle) : Platforms.Home(platform) + "profile/";
                    await page.GotoAsync(ownerUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 60000 }); await Check();
                    peopleDialog = await Site.OpenInstagramFollowing(page);
                }
                await Check(); await Task.Delay(2500, ct); int idle = 0, previous = 0;
                for (int n = 0; n < 60; n++) { await Check(); if (peopleDialog is not null) await peopleDialog.HoverAsync(); else await page.Mouse.MoveAsync(640, 450); await page.Mouse.WheelAsync(0, 900); await Task.Delay(Random.Shared.Next(2000, 5001), ct); await Drain(); scrolls++; idle = previous == batches ? idle + 1 : 0; previous = batches; if (idle >= 3) break; if (n == 59) status = "capped"; }
                if (batches == 0) throw new IOException("No friends/following list response captured; inspect noVNC, list navigation and capture gate");
            }
            else if (options.Mode == "all_followed")
            {
                var targets = await new TimelineCoverage(factory).Select(platform, options.FriendsLimit ?? s.Config.Platform(platform).SweepLimit, ct, options.RetryIncomplete);
                foreach (var target in targets)
                {
                    await using var db = factory.Open(); var author = await db.Authors.FindAsync([target.AuthorId], ct);
                    if (author is null || !author.IsFriend || author.Url is null) { await db.SweepTargets.Where(t => t.Platform == platform && t.CycleId == target.CycleId && t.AuthorId == target.AuthorId).ExecuteUpdateAsync(u => u.SetProperty(t => t.State, "skipped"), ct); continue; }
                    await db.SweepTargets.Where(t => t.Platform == platform && t.CycleId == target.CycleId && t.AuthorId == target.AuthorId).ExecuteUpdateAsync(u => u.SetProperty(t => t.State, "visiting").SetProperty(t => t.RunId, run.Id).SetProperty(t => t.AttemptedAt, Clock.Now), ct);
                    await Visit(author, author.Url, Math.Clamp(options.TimelineScrolls ?? 2, 2, 5), target);
                }
            }
            else if (options.Person is not null)
            {
                var url = Site.Timeline(platform, options.Person); var reference = Identity.UrlRef(platform, url);
                await using var db = factory.Open(); var authors = await db.Authors.Where(a => a.Platform == platform).ToArrayAsync(ct);
                await Visit(reference is null ? null : authors.FirstOrDefault(a => Identity.Keys(a).Contains(reference)), url, options.Scrolls);
            }
            else
            {
                var home = await Surface(Platforms.Home(platform), false, options.Scrolls); status = home.Stop == "depth" ? "capped" : "ok";
                if (options.Mode == "close_friends" || !s.Config.Platform(platform).HomeTimelineAuthors.IsEmpty)
                {
                    await using var db = factory.Open(); var authors = await db.Authors.Where(a => a.Platform == platform && a.IsFriend).ToListAsync(ct);
                    foreach (var author in HomeTimelines(platform, s.Config.Platform(platform), options, authors)) await Visit(author, author.Url!, Math.Clamp(options.TimelineScrolls ?? 2, 2, 5));
                }
            }
            await Drain(); session.ExportCookiesOnClose(); await Relogin(platform, false, ct);
        }
        catch (CheckpointException) { status = "checkpoint"; error = "checkpoint: needs re-login"; await Relogin(platform, true, ct); }
        catch (ResourceBusyException) { status = "refused"; error = "platform busy"; }
        catch (Exception e) when (e is not OperationCanceledException) { status = "error"; error = e.Message; }
        finally { try { await Drain(); } catch (Exception e) when (e is not OperationCanceledException) { listenerErrors++; error ??= e.Message; } }
        await ledger.Phase(run.Id, "ingest", ct); string ingestStatus = "pending"; int ingestFailures = 0;
        using (var ingestLock = ResourceLock.Try(paths, "ingest-" + platform)) if (ingestLock is not null)
        {
            foreach (var file in Directory.EnumerateFiles(directory).Where(f => Path.GetExtension(f) is ".json" or ".jsonl").Order()) { var result = await ingest.File(platform, file, s, false, ct); run.PostsFound += result.Found; run.PostsNew += result.New; ingestFailures += result.Failed; }
            ingestStatus = ingestFailures == 0 ? "done" : "failed";
        }
        if (status is "ok" or "capped" && (ingestFailures > 0 || listenerErrors > 0)) { status = "error"; error ??= $"capture/ingest incomplete: {listenerErrors} listener/parser warning(s), {ingestFailures} failed raw file(s)"; }
        run.Error = error; run.StatsJson = JsonSerializer.Serialize(new { scrolls, batches, visited, incomplete, empty = emptyVisits, unrendered, listener_errors = listenerErrors, ingest = ingestStatus, complete = false, prune_skipped_reason = friends ? "adapter has no proven live completion evidence; add-only" : null });
        Console.WriteLine($"{(friends ? "friends" : "collect")} {platform} {options.Mode}: {status} run=#{run.Id} (found={run.PostsFound}, new={run.PostsNew}, scrolls={scrolls}, batches={batches}, visited={visited}, incomplete={incomplete}, empty={emptyVisits}, unrendered={unrendered}); ingest={ingestStatus}; coverage={(friends ? "complete=false; pruned=0" : "bounded observations")} [error={error}]");
        return status;
    }
    sealed class CheckpointException : Exception;
}
