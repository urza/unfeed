using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public sealed record WorkScope(string Kind = "process", string Platform = "all", int? Limit = null, bool All = false, long? PostId = null, DateTime? Since = null, string? Author = null, bool TextOnly = false);
public sealed class StageCounts { public int Selected; public int Completed; public int Failed; public int Superseded; public int Undispatched; public int Remaining; }
public sealed class WorkCounts
{
    public ConcurrentDictionary<string, StageCounts> Stages { get; } = new();
    public StageCounts Stage(string platform, string task) => Stages.GetOrAdd(platform + "/" + task, _ => new());
    public int Selected; public int Completed; public int Failed; public int Superseded; public int Undispatched; public int Active; public int Prepared; public int Unapplied;
    public override string ToString() => $"selected={Selected}, completed={Completed}, failed={Failed}, superseded={Superseded}, selected-but-not-dispatched={Undispatched}, active={Active}, prepared={Prepared}, unapplied={Unapplied}";
}
public sealed class Processing(InstancePaths paths, DbFactory factory, ModelClient model, MediaFiles media, Ingest ingest)
{
    sealed record Batch(int Id, string Platform, string Task, int Count) { public int Settled; public int Success; public int Failure; }
    sealed record Prepared(Post Post, RuleContext Rules, string Task, Batch Batch, object Messages, string Hash, bool Inline);
    sealed record Result(Prepared Work, object? Value, string? Endpoint, string? Error, bool Sent);
    public async Task<WorkCounts> Run(InstanceSnapshot instance, WorkScope scope, Action<string>? progress, CancellationToken ct, Func<WorkCounts, Task>? report = null)
    {
        using var ownership = ResourceLock.Try(paths, "processing") ?? throw new ResourceBusyException("processing busy");
        var counts = new WorkCounts();
        await using var reporting = new WorkReporter(counts, report);
        string[] platforms = scope.Platform == "all" ? Platforms.All.Where(instance.Config.Enabled).ToArray() : [scope.Platform];
        foreach (var platform in platforms) foreach (var task in new[] { "raw", "judge", "summary", "video" }) counts.Stage(platform, task);
        using var recoveryProgress = new SemaphoreSlim(0);
        using var recoveryCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        async Task Recover()
        {
            try
            {
                if (scope.Kind != "process") return;
                int admitted = 0; var reserved = new HashSet<long>();
                foreach (var platform in platforms) await ingest.Discover(platform, recoveryCancellation.Token);
                bool found;
                do
                {
                    found = false;
                    foreach (var platform in platforms)
                    {
                        if (admitted >= (scope.Limit ?? int.MaxValue)) return;
                        using var ingestLock = ResourceLock.Try(paths, "ingest-" + platform); if (ingestLock is null) continue;
                        await using var db = factory.Open(); var retry = Clock.Now.AddMinutes(-30); var excluded = reserved.ToArray();
                        var raws = await db.RawSnapshots.Where(r => r.Platform == platform && !r.Parsed && !r.Deleted && r.BlockedParserVersion != PayloadParser.Version && !excluded.Contains(r.Id) && !db.Runs.Any(run => run.Id == r.RunId && run.Status == "running" && run.Phase == "browser") && (r.AttemptedAt == null || r.AttemptedAt <= retry)).OrderBy(r => r.AttemptedAt ?? r.CapturedAt).ThenBy(r => r.Id).Take(Math.Min(instance.Config.Llm.Batch, (scope.Limit ?? int.MaxValue) - admitted)).ToListAsync(recoveryCancellation.Token);
                        foreach (var raw in raws)
                        {
                            reserved.Add(raw.Id); admitted++; found = true; Interlocked.Increment(ref counts.Stage(platform, "raw").Selected);
                            var result = await ingest.File(platform, paths.Get(raw.Path), instance, false, recoveryCancellation.Token);
                            Interlocked.Add(ref counts.Failed, result.Failed); if (result.Failed > 0) Interlocked.Increment(ref counts.Stage(platform, "raw").Failed); else Interlocked.Increment(ref counts.Stage(platform, "raw").Completed); progress?.Invoke($"process {platform} ingest: selected={admitted}, found={result.Found}, new={result.New}, failed={result.Failed}"); recoveryProgress.Release();
                        }
                    }
                } while (found);
            }
            catch { recoveryCancellation.Cancel(); throw; }
            finally { recoveryProgress.Release(); }
        }
        // Recovery has independent contexts/IO and bounded platform turns, so slow images cannot stall ready HTTP work.
        var recovery = Recover();
        bool modelFinished = !instance.Config.Llm.Enabled;
        async Task Videos()
        {
            if (scope.Kind != "process") return;
            using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; var maintenance = new Maintenance(paths, factory, http); int admitted = 0;
            while (true)
            {
                bool found = false;
                foreach (var platform in platforms)
                {
                    if (admitted >= (scope.Limit ?? int.MaxValue)) return;
                    var n = await maintenance.Videos(instance, [platform], Math.Min(instance.Config.Llm.Batch, (scope.Limit ?? int.MaxValue) - admitted), recoveryCancellation.Token); admitted += n.Selected; found |= n.Selected > 0; var stage = counts.Stage(platform, "video"); Interlocked.Add(ref stage.Selected, n.Selected); Interlocked.Add(ref stage.Completed, n.Completed); Interlocked.Add(ref stage.Failed, n.Failed); Interlocked.Add(ref counts.Failed, n.Failed);
                }
                if (!found) { if (Volatile.Read(ref modelFinished) && recovery.IsCompleted) break; await Task.Delay(200, recoveryCancellation.Token); }
            }
        }
        async Task GuardVideos() { try { await Videos(); } catch { recoveryCancellation.Cancel(); throw; } }
        var videoWork = GuardVideos();
        if (!instance.Config.Llm.Enabled) { try { await recovery; await videoWork; await Remaining(); return counts; } finally { recoveryCancellation.Cancel(); try { await Task.WhenAll(recovery, videoWork); } catch when (ct.IsCancellationRequested) { } } }
        var configHash = Prompts.ConfigurationHash(instance, scope.TextOnly); var version = Prompts.Version(instance, scope.TextOnly);
        var capacity = Math.Max(2, instance.Config.Llm.Parallel * 2);
        var inputs = Channel.CreateBounded<Prepared>(new BoundedChannelOptions(capacity) { SingleWriter = true });
        var results = Channel.CreateBounded<Result>(new BoundedChannelOptions(capacity) { SingleReader = true });
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(recoveryCancellation.Token); var token = cancellation.Token;
        using var dispatchGate = new SemaphoreSlim(1);
        var stopped = new ConcurrentDictionary<string, bool>(); var selected = new HashSet<(string Task, long Id)>(); var budgets = new Dictionary<string, int>(); int batchId = 0;
        var turns = platforms.SelectMany(p => scope.Kind == "rescore" ? new[] { (Platform: p, Task: "judge") } : scope.Kind == "summarize" ? [(p, "summary")] : [(p, "judge"), (p, "summary")]).ToArray();
        async Task Feed()
        {
            try
            {
                if (turns.Length == 0) return;
                await using var cursorDb = factory.Open(); var cursorText = await cursorDb.Get("process:turn", token); int cursor = int.TryParse(cursorText, out var saved) ? saved % turns.Length : 0; int empty = 0;
                while (true)
                {
                    if (empty >= turns.Length)
                    {
                        if (recovery.IsCompleted) { await recovery; break; }
                        await recoveryProgress.WaitAsync(token); empty = 0;
                    }
                    token.ThrowIfCancellationRequested(); var turn = turns[cursor]; cursor = (cursor + 1) % turns.Length; await cursorDb.Put("process:turn", cursor.ToString(), token);
                    if (stopped.ContainsKey(turn.Task) || budgets.GetValueOrDefault(turn.Task) >= (scope.Limit ?? int.MaxValue)) { empty++; continue; }
                    await using var db = factory.Open();
                    var backoff = await db.Get($"model:{configHash}:{turn.Task}:not_before", token);
                    if (scope.Kind == "process" && DateTime.TryParse(backoff, out var until) && until.ToUniversalTime() > Clock.Now) { empty++; continue; }
                    var query = db.Posts.AsNoTracking().Where(p => p.Platform == turn.Platform && p.IngestReadyAt != null);
                    if (turn.Task == "judge") query = query.Where(p => !p.Hidden || p.HiddenBy == "llm"); else query = query.Where(p => !p.Hidden);
                    if (scope.PostId is { } id) query = query.Where(p => p.Id == id);
                    else if (turn.Task == "judge") query = query.Where(p => p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision || (scope.All || scope.Author != null) && p.PrefsVersion != version);
                    else query = query.Where(p => p.SummaryContentRevision != p.ContentRevision || p.Summary == null);
                    if (scope.Since is { } since) query = query.Where(p => p.PostedAt >= since);
                    if (scope.Author is { } authorRef) { var authorId = await db.AuthorKeys.Where(k => k.Key == authorRef.ToLowerInvariant()).Select(k => (long?)k.AuthorId).SingleOrDefaultAsync(token); if (authorId is null) throw new ArgumentException("Unknown author ref"); query = query.Where(p => p.AuthorId == authorId); }
                    if (scope.Kind == "process") { var retry = Clock.Now.AddMinutes(-30); query = turn.Task == "judge" ? query.Where(p => p.LlmAttemptedAt == null || p.LlmAttemptedAt <= retry).OrderBy(p => p.LlmAttemptedAt ?? p.CapturedAt).ThenBy(p => p.Id) : query.Where(p => p.SummaryAttemptedAt == null || p.SummaryAttemptedAt <= retry).OrderBy(p => p.SummaryAttemptedAt ?? p.CapturedAt).ThenBy(p => p.Id); }
                    else query = query.OrderByDescending(p => p.PostedAt).ThenByDescending(p => p.Id);
                    var exclude = selected.Where(x => x.Task == turn.Task).Select(x => x.Id).ToArray(); query = query.Where(p => !exclude.Contains(p.Id));
                    var take = Math.Min(instance.Config.Llm.Batch, (scope.Limit ?? int.MaxValue) - budgets.GetValueOrDefault(turn.Task)); var posts = await query.Take(take).ToListAsync(token);
                    if (posts.Count == 0) { empty++; continue; } empty = 0;
                    var batch = new Batch(++batchId, turn.Platform, turn.Task, posts.Count); budgets[turn.Task] = budgets.GetValueOrDefault(turn.Task) + posts.Count;
                    foreach (var p in posts) { selected.Add((turn.Task, p.Id)); Interlocked.Increment(ref counts.Selected); Interlocked.Increment(ref counts.Stage(p.Platform, turn.Task).Selected); }
                    foreach (var p in posts)
                    {
                        if (stopped.ContainsKey(turn.Task)) { Interlocked.Increment(ref counts.Undispatched); Interlocked.Increment(ref counts.Stage(p.Platform, turn.Task).Undispatched); continue; }
                        try
                        {
                            var authors = await db.Authors.AsNoTracking().ToListAsync(token); var rules = new RuleContext(authors.FirstOrDefault(a => a.Id == p.AuthorId), authors, instance);
                            var files = await db.Media.AsNoTracking().Where(m => m.PostId == p.Id && m.IsCurrent).OrderBy(m => m.Position).ThenBy(m => m.Id).ToListAsync(token);
                            var feedback = p.AuthorId is null ? [] : await db.Feedback.AsNoTracking().Where(f => f.AuthorId == p.AuthorId).OrderByDescending(f => f.Id).Take(200).ToListAsync(token);
                            var messages = new List<object>(); bool inline = turn.Task == "summary" && !Prompts.NeedsSummary(p);
                            if (turn.Task == "judge")
                            {
                                var text = Prompts.Policy(instance) + "\n\n" + Prompts.ReplyShape(instance.Taxonomy) + "\n\n" + Prompts.PostJson(p, rules, files, Prompts.Memory(feedback, p.Id, p.AuthorId));
                                var parts = new List<object> { new { type = "text", text } };
                                if (instance.Config.Llm.Vision && !scope.TextOnly) foreach (var file in files.Where(m => m.Kind == "image" && m.Path != null)) { if (parts.Count - 1 >= instance.Config.Llm.VisionMaxImages) break; var image = await media.Vision(file.Path!, token); if (image is { } data) parts.Add(new { type = "image_url", image_url = new { url = $"data:{data.Mime};base64,{Convert.ToBase64String(data.Bytes)}" } }); }
                                messages.Add(new { role = "system", content = Prompts.System }); messages.Add(new { role = "user", content = parts });
                            }
                            else
                            {
                                var languages = instance.Config.Llm.SummaryLanguages; var language = languages.Length == 0 ? "Write the summary in English." : $"If the post is in one of these languages: {string.Join(", ", languages)}, write the summary in the language of the post. If the post is in any other language, write the summary in English.";
                                messages.Add(new { role = "system", content = $"You summarize social media posts for a private chronological feed. Reply with ONLY a 1-2 sentence summary of the post. {language} Refer to the author by their name, never as 'the author'. No preamble, no markdown, do not quote the post." }); messages.Add(new { role = "user", content = $"Post by {rules.Author?.DisplayName ?? p.ObservedAuthorName ?? "the author"}:\n{p.DisplayText[..Math.Min(4000, p.DisplayText.Length)]}" });
                            }
                            var hash = Prompts.Sha(JsonSerializer.Serialize(ModelClient.RequestBody(instance.Config.Llm, messages, turn.Task == "judge" ? instance.Config.Llm.MaxTokens : instance.Config.Llm.SummaryMaxTokens, turn.Task == "summary" ? instance.Config.Llm.SummaryEnableThinking : null)));
                            var prepared = new Prepared(p, rules, turn.Task, batch, messages, hash, inline);
                            if (inline) { Interlocked.Increment(ref counts.Unapplied); await results.Writer.WriteAsync(new(prepared, "", null, null, false), token); }
                            else { Interlocked.Increment(ref counts.Prepared); await inputs.Writer.WriteAsync(prepared, token); }
                        }
                        catch (Exception e) when (e is not OperationCanceledException)
                        {
                            var work = new Prepared(p, new(null, [], instance), turn.Task, batch, Array.Empty<object>(), "", false);
                            await Stamp(work, token); Interlocked.Increment(ref counts.Unapplied); await results.Writer.WriteAsync(new(work, null, null, e.Message, false), token);
                        }
                    }
                }
            }
            catch { cancellation.Cancel(); recoveryCancellation.Cancel(); throw; }
            finally { inputs.Writer.TryComplete(); }
        }
        async Task<bool> Stamp(Prepared work, CancellationToken cancel)
        {
            await using var db = factory.Open(); var p = work.Post; var eligible = db.Posts.Where(x => x.Id == p.Id && x.ContentRevision == p.ContentRevision && x.VisibilityRevision == p.VisibilityRevision && x.IngestReadyAt != null && (!x.Hidden || work.Task == "judge" && x.HiddenBy == "llm"));
            return (work.Task == "judge" ? await eligible.ExecuteUpdateAsync(s => s.SetProperty(x => x.LlmAttemptedAt, Clock.Now), cancel) : await eligible.ExecuteUpdateAsync(s => s.SetProperty(x => x.SummaryAttemptedAt, Clock.Now), cancel)) == 1;
        }
        async Task Worker()
        {
            try
            {
                await foreach (var work in inputs.Reader.ReadAllAsync(token))
                {
                    Interlocked.Decrement(ref counts.Prepared);
                    bool authorized = false, closed;
                    await dispatchGate.WaitAsync(token);
                    try { closed = stopped.ContainsKey(work.Task); if (!closed) authorized = await Stamp(work, token); }
                    finally { dispatchGate.Release(); }
                    if (closed) { Interlocked.Increment(ref counts.Undispatched); Interlocked.Increment(ref counts.Stage(work.Post.Platform, work.Task).Undispatched); continue; }
                    if (!authorized) { Interlocked.Increment(ref counts.Unapplied); await results.Writer.WriteAsync(new(work, null, null, null, false), token); continue; }
                    Result result; Interlocked.Increment(ref counts.Active);
                    try
                    {
                        if (work.Task == "judge") { var response = await model.Call(instance.Config.Llm, work.Messages, instance.Config.Llm.MaxTokens, s => Prompts.ParseVerdict(s, instance.Taxonomy), token); result = new(work, response.Value, response.Endpoint, null, true); }
                        else { var response = await model.Call(instance.Config.Llm, work.Messages, instance.Config.Llm.SummaryMaxTokens, s => string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), token, instance.Config.Llm.SummaryEnableThinking); result = new(work, response.Value, response.Endpoint, null, true); }
                    }
                    catch (Exception e) when (e is not OperationCanceledException || !token.IsCancellationRequested) { result = new(work, null, null, e.Message, true); }
                    finally { Interlocked.Decrement(ref counts.Active); }
                    Interlocked.Increment(ref counts.Unapplied); await results.Writer.WriteAsync(result, token);
                }
            }
            catch { cancellation.Cancel(); recoveryCancellation.Cancel(); throw; }
        }
        async Task Apply()
        {
            try
            {
                await foreach (var result in results.Reader.ReadAllAsync(token))
                {
                    Interlocked.Decrement(ref counts.Unapplied); var w = result.Work; var batch = w.Batch; var stage = counts.Stage(w.Post.Platform, w.Task);
                    if (result.Error is not null)
                    {
                        counts.Failed++; stage.Failed++; batch.Failure++;
                        await using var db = factory.Open();
                        var current = db.Posts.Where(p => p.Id == w.Post.Id && p.ContentRevision == w.Post.ContentRevision && p.VisibilityRevision == w.Post.VisibilityRevision);
                        if (w.Task == "judge") await current.ExecuteUpdateAsync(u => u.SetProperty(p => p.LlmError, result.Error).SetProperty(p => p.LlmFailures, p => p.LlmFailures + 1), token);
                        else await current.ExecuteUpdateAsync(u => u.SetProperty(p => p.SummaryError, result.Error).SetProperty(p => p.SummaryFailures, p => p.SummaryFailures + 1), token);
                        progress?.Invoke($"post #{w.Post.Id} {w.Task} failed; retry after 30 minutes: {result.Error}");
                    }
                    else if (result.Value is null) { counts.Superseded++; stage.Superseded++; batch.Success++; }
                    else
                    {
                        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync(token);
                        var p = await db.Posts.SingleAsync(p => p.Id == w.Post.Id, token);
                        if (p.ContentRevision != w.Post.ContentRevision || p.VisibilityRevision != w.Post.VisibilityRevision || p.Hidden && (w.Task == "summary" || p.HiddenBy != "llm")) { counts.Superseded++; stage.Superseded++; }
                        else
                        {
                            if (result.Value is Verdict verdict)
                            {
                                p.LlmError = null; p.LlmFailures = 0; p.LlmScore = verdict.Score; p.LlmReason = verdict.Reason; p.CategoriesJson = JsonSerializer.Serialize(Filters.Restrict(verdict.Categories, w.Rules, p.Platform)); p.VerdictContentRevision = p.ContentRevision; p.PrefsVersion = version; p.VerdictInputHash = w.Hash; p.VerdictModel = instance.Config.Llm.Model; p.VerdictEndpoint = result.Endpoint; p.JudgedAt = Clock.Now;
                                if (verdict.Score < instance.Config.Llm.Threshold && !w.Rules.Shield("llm")) p.SetHidden("llm", $"llm {verdict.Score}: {verdict.Reason}"); else if (p.HiddenBy == "llm") p.ClearHidden();
                            }
                            else { p.SummaryError = null; p.SummaryFailures = 0; p.Summary = (string)result.Value; p.SummaryContentRevision = p.ContentRevision; p.SummaryInputHash = w.Hash; p.SummaryModel = w.Inline ? null : instance.Config.Llm.Model; p.SummaryEndpoint = result.Endpoint; p.SummarizedAt = Clock.Now; }
                            await db.SaveChangesAsync(token); counts.Completed++; stage.Completed++;
                        }
                        await tx.CommitAsync(token); batch.Success++;
                    }
                    batch.Settled++;
                    if (batch.Settled == batch.Count)
                    {
                        if (batch.Failure > 0 && batch.Success == 0) { await dispatchGate.WaitAsync(token); try { stopped[batch.Task] = true; } finally { dispatchGate.Release(); } await using var db = factory.Open(); await db.Put($"model:{configHash}:{batch.Task}:not_before", Clock.Now.AddMinutes(30).ToString("O"), token); }
                        progress?.Invoke($"batch #{batch.Id} {batch.Platform}/{batch.Task}: {counts}");
                    }
                }
            }
            catch { cancellation.Cancel(); recoveryCancellation.Cancel(); throw; }
        }
        var apply = Apply(); var workers = Enumerable.Range(0, instance.Config.Llm.Parallel).Select(_ => Worker()).ToArray(); var feed = Feed();
        try { await feed; await Task.WhenAll(workers); results.Writer.TryComplete(); await apply; Volatile.Write(ref modelFinished, true); await recovery; await videoWork; }
        finally { cancellation.Cancel(); recoveryCancellation.Cancel(); inputs.Writer.TryComplete(); results.Writer.TryComplete(); try { await Task.WhenAll(workers.Append(feed).Append(apply).Append(recovery).Append(videoWork)); } catch when (ct.IsCancellationRequested) { } }
        await Remaining();
        async Task Remaining()
        {
        await using (var db = factory.Open()) foreach (var platform in platforms)
        {
            counts.Stage(platform, "judge").Remaining = await db.Posts.CountAsync(p => p.Platform == platform && (!p.Hidden || p.HiddenBy == "llm") && (p.CategoriesJson == null || p.VerdictContentRevision != p.ContentRevision), ct);
            counts.Stage(platform, "summary").Remaining = await db.Posts.CountAsync(p => p.Platform == platform && !p.Hidden && (p.Summary == null || p.SummaryContentRevision != p.ContentRevision), ct);
            counts.Stage(platform, "raw").Remaining = await db.RawSnapshots.CountAsync(r => r.Platform == platform && !r.Deleted && !r.Parsed, ct);
            counts.Stage(platform, "video").Remaining = await db.Media.Join(db.Posts.Where(p => p.Platform == platform), m => m.PostId, p => p.Id, (m, p) => m).CountAsync(m => m.IsCurrent && m.Kind == "video" && m.Path == null && m.PrunedAt == null, ct);
        }
        }
        return counts;
    }
}
public sealed class ResourceBusyException(string message) : Exception(message);

sealed class WorkReporter : IAsyncDisposable
{
    readonly CancellationTokenSource stop = new(); readonly Task loop;
    readonly WorkCounts counts; readonly Func<WorkCounts, Task>? report;
    public WorkReporter(WorkCounts counts, Func<WorkCounts, Task>? report)
    {
        this.counts = counts; this.report = report; loop = Poll();
    }
    async Task Publish() { if (report is not null) try { await report(counts); } catch (Exception e) { Console.Error.WriteLine("progress report unavailable: " + e.Message); } }
    async Task Poll()
    {
        if (report is null) return;
        try { while (!stop.IsCancellationRequested) { await Publish(); await Task.Delay(2000, stop.Token); } }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); await loop; await Publish(); stop.Dispose(); }
}
