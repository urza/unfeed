using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class ModelSafetyTests
{
    sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> fn):HttpMessageHandler{protected override Task<HttpResponseMessage>SendAsync(HttpRequestMessage r,CancellationToken c)=>fn(r,c);}
    static HttpResponseMessage Reply(string content)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{message=new{content}}}}),Encoding.UTF8,"application/json")};
    static InstanceSnapshot Instance=>new(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()).Add("instagram",new()),Llm=new(){Enabled=true,BaseUrl="http://primary.test/v1",FallbackBaseUrl="",Vision=false,Batch=2,Parallel=2}},new(),Preferences.Parse(""));
    [Theory] [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task SummaryRequestsPreserveWhoWroteEachPart(bool shared, bool unknownOriginal)
    {
        await using var i = new TestInstance(); await i.Init();
        const string story = "I am changing careers.\nLooking for a new role.\nPlease send suggestions.";
        await using (var db = i.Factory.Open()) {
            db.Add(new Post { Platform = "facebook", PlatformPostId = "fixture", ObservedAuthorName = "Synthetic Sharer", Text = shared ? "Please help my friend." : story,
                SharedAuthor = shared && !unknownOriginal ? "Synthetic Original" : null, SharedText = shared ? story : null, IngestReadyAt = Clock.Now });
            await db.SaveChangesAsync();
        }
        string? requestBody = null;
        using var handler = new Handler(async (r, ct) => { requestBody = await r.Content!.ReadAsStringAsync(ct); return Reply("Synthetic summary."); });
        using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http);
        var counts = await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(Instance, new("summarize"), null, default);
        Assert.Equal(1, counts.Completed);
        using var request = JsonDocument.Parse(requestBody!);
        var messages = request.RootElement.GetProperty("messages");
        Assert.Contains("never to the sharer", messages[0].GetProperty("content").GetString());
        using var body = JsonDocument.Parse(messages[1].GetProperty("content").GetString()!);
        Assert.Equal("Synthetic Sharer", body.RootElement.GetProperty("author").GetString());
        Assert.Equal(shared ? "Please help my friend." : story, body.RootElement.GetProperty("text").GetString());
        if (shared) {
            var original = body.RootElement.GetProperty("shared");
            Assert.Equal(unknownOriginal ? null : "Synthetic Original", original.GetProperty("author").GetString());
            Assert.Equal(story, original.GetProperty("text").GetString());
        } else Assert.False(body.RootElement.TryGetProperty("shared", out _));
        await using var check = i.Factory.Open(); Assert.Equal(Prompts.Sha(requestBody!), (await check.Posts.SingleAsync()).SummaryInputHash);
    }
    [Fact] public async Task SelectedRescoreDoesNotTouchOtherPostsAndAcceptsEmptyCategories()
    {
        await using var i = new TestInstance(); await i.Init(); long id;
        var snapshot = Instance with { Taxonomy = new() { Categories = [new() { Key = "topic", Label = "Topic" }] } };
        await using (var db = i.Factory.Open()) {
            var selected = new Post { Platform = "instagram", PlatformPostId = "selected", IngestReadyAt = Clock.Now, Hidden = true, HiddenBy = "llm", CategoriesJson = "[\"topic\"]", VerdictContentRevision = 1, PrefsVersion = Prompts.Version(snapshot) };
            db.AddRange(selected, new Post { Platform = "instagram", PlatformPostId = "untouched", IngestReadyAt = Clock.Now }); await db.SaveChangesAsync(); id = selected.Id;
        }
        using var handler = new Handler((_, _) => Task.FromResult(Reply("{\"score\":7,\"reason\":\"No matching category\",\"categories\":[]}")));
        using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http);
        var counts = await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(snapshot, new("rescore", PostIds: [id]), null, default);
        Assert.Equal(1, counts.Completed);
        await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync(p => p.Id == id);
        Assert.False(post.Hidden); Assert.True(post.Judged); Assert.Equal("[]", post.CategoriesJson);
        Assert.Null((await check.Posts.SingleAsync(p => p.Id != id)).CategoriesJson);
    }
    [Fact] public async Task OptionalSummaryThinkingSettingChangesOnlySummaryRequestsAndProvenance()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open()) { db.Add(new Post { Platform = "facebook", PlatformPostId = "fixture", Text = "First paragraph\nSecond paragraph\nThird paragraph", IngestReadyAt = Clock.Now }); await db.SaveChangesAsync(); }
        var configured = Instance with { Config = Instance.Config with { Llm = Instance.Config.Llm with { SummaryEnableThinking = false } } };
        Assert.NotEqual(Prompts.ConfigurationHash(Instance), Prompts.ConfigurationHash(configured));
        var requests = new System.Collections.Concurrent.ConcurrentBag<(bool Summary, string Body)>();
        using var handler = new Handler(async (r, ct) => {
            var body = await r.Content!.ReadAsStringAsync(ct); var summary = body.Contains("You summarize"); requests.Add((summary, body));
            return Reply(summary ? "A synthetic summary." : "{\"score\":9,\"reason\":\"synthetic\"}");
        }); using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http);
        await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(configured, new(), null, default);
        var summaryRequest = Assert.Single(requests, r => r.Summary).Body; var judgeRequest = Assert.Single(requests, r => !r.Summary).Body;
        using var json = JsonDocument.Parse(summaryRequest); Assert.False(json.RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.DoesNotContain("chat_template_kwargs", judgeRequest);
        await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync(); Assert.Equal(Prompts.Sha(summaryRequest), post.SummaryInputHash); Assert.Equal(Prompts.Sha(judgeRequest), post.VerdictInputHash);
        Assert.DoesNotContain("chat_template_kwargs", JsonSerializer.Serialize(ModelClient.RequestBody(Instance.Config.Llm, Array.Empty<object>(), 4000)));
    }
    [Fact]public async Task FallbackUsesSameInputAndRecordsServingEndpoint()
    {
        var bodies=new List<string>();using var handler=new Handler(async(r,c)=>{bodies.Add(await r.Content!.ReadAsStringAsync(c));return Reply(r.RequestUri!.Host=="primary.test"?"bad":"{\"score\":9,\"reason\":\"ok\"}");});using var http=new HttpClient(handler);
        var result=await new ModelClient(http).Call(Instance.Config.Llm with{FallbackBaseUrl="http://fallback.test/v1"},new[]{new{role="user",content="synthetic"}},4000,s=>Prompts.ParseVerdict(s,new()),default);Assert.Equal("http://fallback.test/v1",result.Endpoint);Assert.Equal(2,bodies.Count);Assert.Equal(bodies[0],bodies[1]);
    }
    [Fact]public async Task ClientErrorDoesNotFallback()
    {
        int calls=0;using var handler=new Handler((_,_)=>{calls++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));});using var http=new HttpClient(handler);await Assert.ThrowsAsync<HttpRequestException>(()=>new ModelClient(http).Call(Instance.Config.Llm with{FallbackBaseUrl="http://fallback.test/v1"},Array.Empty<object>(),4000,s=>s,default));Assert.Equal(1,calls);
    }
    [Fact]public async Task InflightVerdictCannotOverwriteOwnerUnhide()
    {
        await using var i=new TestInstance();await i.Init();long id;await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="synthetic",IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();id=p.Id;}
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var handler=new Handler(async(_,ct)=>{entered.TrySetResult();await release.Task.WaitAsync(ct);return Reply("{\"score\":0,\"reason\":\"synthetic hide\"}");});using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var work=new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media)).Run(Instance,new("rescore"),null,timeout.Token);await entered.Task.WaitAsync(timeout.Token);await new Actions(i.Factory).Unhide(id);release.TrySetResult();var counts=await work;Assert.Equal(1,counts.Superseded);await using var check=i.Factory.Open();var post=await check.Posts.FindAsync(id);Assert.False(post!.Hidden);Assert.Null(post.CategoriesJson);
    }
    [Fact]public async Task PerStageLimitCountsInlineSummariesAcrossPlatforms()
    {
        await using var i=new TestInstance();await i.Init();await using(var db=i.Factory.Open()){foreach(var platform in Platforms.All)for(int n=0;n<5;n++)db.Add(new Post{Platform=platform,PlatformPostId=n.ToString(),Text="short synthetic text",IngestReadyAt=Clock.Now});await db.SaveChangesAsync();}
        int calls=0;using var handler=new Handler((_,_)=>{Interlocked.Increment(ref calls);return Task.FromResult(Reply("{\"score\":9,\"reason\":\"fine\"}"));});using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);var counts=await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media)).Run(Instance,new(Limit:1),null,default);Assert.Equal(2,counts.Selected);Assert.Equal(1,calls);await using var check=i.Factory.Open();Assert.Equal(1,await check.Posts.CountAsync(p=>p.SummaryContentRevision==p.ContentRevision));Assert.Equal(1,await check.Posts.CountAsync(p=>p.VerdictContentRevision==p.ContentRevision));
    }
    [Fact]public async Task FailedBatchBacksOffAndLeavesUnjudgedRowsVisible()
    {
        await using var i=new TestInstance();await i.Init();await using(var db=i.Factory.Open()){for(int n=0;n<20;n++)db.Add(new Post{Platform="facebook",PlatformPostId=n.ToString(),Text="synthetic",IngestReadyAt=Clock.Now});await db.SaveChangesAsync();}
        using var handler=new Handler((_,_)=>Task.FromResult(Reply("invalid")));using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);var counts=await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media)).Run(Instance,new("rescore"),null,default);Assert.True(counts.Failed>0);await using var check=i.Factory.Open();Assert.Equal(20,await check.Posts.CountAsync(p=>!p.Hidden&&p.CategoriesJson==null));Assert.NotNull(await check.Get($"model:{Prompts.ConfigurationHash(Instance)}:judge:not_before"));Assert.True(await check.Posts.AnyAsync(p=>p.LlmAttemptedAt==null));
    }

    [Fact] public async Task CancellationObservesHttpAndReleasesOwnershipWithoutOutageBackoff()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open()) { for (int n=0;n<50;n++) db.Add(new Post { Platform="facebook", PlatformPostId=n.ToString(), Text="synthetic", IngestReadyAt=Clock.Now }); await db.SaveChangesAsync(); }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); int active=0, calls=0;
        using var handler = new Handler(async (_, ct) => { Interlocked.Increment(ref calls); Interlocked.Increment(ref active); entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); return Reply("unused"); } finally { Interlocked.Decrement(ref active); } });
        using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths,http); using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var worker = new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media));
        var run = worker.Run(Instance,new("rescore"),null,cancel.Token); await entered.Task.WaitAsync(cancel.Token); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run); Assert.Equal(0,active); Assert.InRange(calls,1,2); Assert.False(ResourceLock.Busy(i.Paths,"processing"));
        await using var check=i.Factory.Open(); Assert.Null(await check.Get("process:not_before")); Assert.Null(await check.Get($"model:{Prompts.ConfigurationHash(Instance)}:judge:not_before")); Assert.True(await check.Posts.AnyAsync(p=>p.LlmAttemptedAt==null));
        // A fresh invocation can immediately acquire ownership with its own configuration.
        await worker.Run(Instance with { Config=Instance.Config with { Llm=Instance.Config.Llm with { Enabled=false } } },new(),null,default);
    }
    [Fact] public async Task MixedPlatformsAndSummariesRefillOneHttpBudgetAcrossTurns()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db=i.Factory.Open()) { foreach(var platform in Platforms.All) for(int n=0;n<6;n++) db.Add(new Post { Platform=platform, PlatformPostId=n.ToString(), Text=platform+" first line\nsecond line\nthird line", IngestReadyAt=Clock.Now }); await db.SaveChangesAsync(); }
        var held=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var later=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var kinds=new System.Collections.Concurrent.ConcurrentDictionary<string,int>(); int calls=0, active=0, peak=0;
        using var handler=new Handler(async(r,ct)=>{
            var body=await r.Content!.ReadAsStringAsync(ct); bool summary=body.Contains("You summarize"); var platform=body.Contains("instagram")?"instagram":"facebook"; kinds.AddOrUpdate(platform+(summary?"/summary":"/judge"),1,(_,n)=>n+1);
            var count=Interlocked.Increment(ref calls);var outstanding=Interlocked.Increment(ref active);int old;do{old=peak;}while(outstanding>old&&Interlocked.CompareExchange(ref peak,outstanding,old)!=old);
            try { if(count==1) await held.Task.WaitAsync(ct); if(kinds.Count==4&&count>=8)later.TrySetResult(); return Reply(summary?"Synthetic summary.":"{\"score\":9,\"reason\":\"fine\"}"); } finally {Interlocked.Decrement(ref active);}
        }); using var http=new HttpClient(handler);var media=new MediaFiles(i.Paths,http);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var config=Instance with {Config=Instance.Config with {Llm=Instance.Config.Llm with {Batch=1,Parallel=4}}};
        var run=new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media)).Run(config,new(),null,timeout.Token);
        try {await later.Task.WaitAsync(timeout.Token);Assert.False(run.IsCompleted);Assert.InRange(peak,2,4);}finally{held.TrySetResult();}
        var counts=await run;Assert.Equal(24,counts.Completed);Assert.Equal(24,calls);Assert.All(kinds.Values,n=>Assert.Equal(6,n));
    }
}
