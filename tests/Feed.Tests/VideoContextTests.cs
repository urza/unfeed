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

public sealed class VideoContextTests
{
    sealed class Tool(Func<VideoSource, string?, CancellationToken, Task<string>> extract) : IVideoMetadataTool
    {
        public int Calls;
        public Task<string> Extract(VideoSource source, string? cookies, CancellationToken ct) { Interlocked.Increment(ref Calls); return extract(source, cookies, ct); }
    }
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    static readonly VideoSource Source = new("https://www.youtube.com/watch?v=synthetic", "shared", null);
    static readonly VideoContextConfig Config = new() { Enabled = true, CaptionLanguages = ["cs", "en"] };
    static string Metadata => """{"id":"synthetic","title":"Synthetic lesson","description":"A useful explanation","uploader":"Original creator","duration":125,"chapters":[{"title":"Intro","start_time":0}],"subtitles":{"en":[{"ext":"vtt","url":"https://captions.example.test/manual.vtt"}]},"automatic_captions":{"cs-orig":[{"ext":"json3","url":"https://captions.example.test/auto.json3"}]}}""";

    [Theory]
    [InlineData("https://youtu.be/synthetic", true)]
    [InlineData("https://www.youtube.com/watch?v=synthetic&list=ignored", true)]
    [InlineData("https://www.youtube.com/@creator", false)]
    [InlineData("https://www.youtube.com/playlist?list=ignored", false)]
    [InlineData("https://vimeo.com/12345", true)]
    [InlineData("https://youtube.com.evil.test/watch?v=synthetic", false)]
    [InlineData("https://www.facebook.com/person/videos/12345/", true)]
    [InlineData("https://www.instagram.com/reel/synthetic/", true)]
    [InlineData("file:///private/video.mp4", false)]
    public void SelectsVideoDestinationsNotArbitraryLinks(string url, bool eligible)
    {
        Assert.Equal(eligible, VideoContextProvider.Source(new() { SharedUrl = url }, []) is not null);
        Assert.Null(VideoContextProvider.Source(new() { Text = url }, []));
    }
    [Fact] public void NativeVideoNeedsCurrentMediaAndMatchingPlatformPermalink()
    {
        var p = new Post { Platform = "instagram", Permalink = "https://www.instagram.com/p/synthetic/" };
        Assert.Null(VideoContextProvider.Source(p, [new() { Kind = "image" }]));
        Assert.Null(VideoContextProvider.Source(p, [new() { Kind = "video", IsCurrent = false }]));
        Assert.Equal(new(p.Permalink, "post", "instagram"), VideoContextProvider.Source(p, [new() { Kind = "video" }]));
        p.Permalink = "https://www.youtube.com/watch?v=synthetic";
        Assert.Null(VideoContextProvider.Source(p, [new() { Kind = "video" }]));
    }
    [Fact] public async Task ManualCaptionsWinAndSuccessfulContextIsCachedAcrossInstances()
    {
        await using var i = new TestInstance();
        var tool = new Tool((s, c, ct) => { Assert.Null(c); return Task.FromResult(Metadata); });
        using var http = new HttpClient(new Handler(r => { Assert.Equal("https://captions.example.test/manual.vtt", r.RequestUri!.AbsoluteUri); Assert.Null(r.Headers.Authorization); Assert.False(r.Headers.Contains("Cookie")); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("WEBVTT\n\n00:00:00.000 --> 00:00:02.000\n<v Speaker>Hello &amp; welcome</v>\n\n00:00:02.000 --> 00:00:03.000\nHello &amp; welcome again") }); }));
        VideoContext first;
        using (var provider = new VideoContextProvider(i.Paths, http, tool)) first = await provider.Get(Source, Config, default);
        Assert.Equal("Original creator", first.Uploader); Assert.Equal("shared", first.Relation); Assert.Equal(125, first.DurationSeconds);
        Assert.Equal("Hello & welcome again", first.Captions!.Text); Assert.False(first.Captions.Automatic); Assert.Equal("en", first.Captions.Language);
        using (var provider = new VideoContextProvider(i.Paths, http, tool)) Assert.Equal(JsonSerializer.Serialize(first), JsonSerializer.Serialize(await provider.Get(Source, Config, default)));
        Assert.Equal(1, tool.Calls);
    }
    [Fact] public async Task AutomaticCaptionsAreLabeledAndTranslationsAreNotSilentlySelected()
    {
        await using var i = new TestInstance();
        var tool = new Tool((s, c, ct) => Task.FromResult("""{"id":"synthetic","automatic_captions":{"en":[{"ext":"json3","url":"https://captions.example.test/translation"}],"cs-orig":[{"ext":"json3","url":"https://captions.example.test/original"}]}}"""));
        using var http = new HttpClient(new Handler(r => { Assert.EndsWith("/original", r.RequestUri!.AbsoluteUri); return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"events":[{"segs":[{"utf8":"Ahoj "},{"utf8":"světe"}]}]}""") }); }));
        using var provider = new VideoContextProvider(i.Paths, http, tool);
        var result = await provider.Get(Source, Config, default); Assert.True(result.Captions!.Automatic); Assert.Equal("cs-orig", result.Captions.Language); Assert.Equal("Ahoj světe", result.Captions.Text);
        var other = await provider.Get(Source, Config with { CaptionLanguages = ["en"] }, default); Assert.Equal("not_available", other.Captions!.Status); Assert.Equal(2, tool.Calls);
    }
    [Fact] public void CaptionsStripMarkupDeduplicateAndReportTruncation()
    {
        var captions = VideoContextProvider.ParseCaptions("1\n00:00:00,000 --> 00:00:01,000\nHello <b>world</b>\n\n2\n00:00:01,000 --> 00:00:02,000\nworld again", "srt", "en", false);
        Assert.Equal("Hello world again", captions.Text);
        var json = JsonSerializer.Serialize(new { events = new[] { new { segs = new[] { new { utf8 = new string('x', 9000) } } } } });
        var longText = VideoContextProvider.ParseCaptions(json, "json3", "en", true); Assert.Equal(VideoContextProvider.CaptionLimit, longText.Text!.Length); Assert.True(longText.Truncated);
        Assert.Equal("unavailable", VideoContextProvider.ParseCaptions("<html>Error</html>", "vtt", "en", false).Status);
    }
    [Fact] public async Task FailedCaptionFetchPreservesMetadataAndHasShortCacheTtl()
    {
        await using var i = new TestInstance(); var tool = new Tool((s,c,ct) => Task.FromResult(Metadata));
        using var http = new HttpClient(new Handler(r => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden))));
        using var provider = new VideoContextProvider(i.Paths,http,tool);
        var result = await provider.Get(Source,Config,default); Assert.Equal("available",result.Status); Assert.Equal("Synthetic lesson",result.Title); Assert.Equal("unavailable",result.Captions!.Status);
        using var cache = JsonDocument.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(i.Paths.Get("video-context"),"*.json"))));
        Assert.InRange(cache.RootElement.GetProperty("expires_at").GetDateTime(), Clock.Now.AddMinutes(29),Clock.Now.AddMinutes(31));
    }
    [Theory]
    [InlineData("{\"_type\":\"playlist\",\"entries\":[]}")]
    [InlineData("{\"id\":\"different\",\"webpage_url\":\"https://youtu.be/different\"}")]
    [InlineData("not json")]
    public async Task PlaylistRedirectOrMalformedMetadataFailsOpen(string body)
    {
        await using var i = new TestInstance(); var tool = new Tool((s,c,ct) => Task.FromResult(body));
        using var provider = new VideoContextProvider(i.Paths,tool:tool);
        var result = await provider.Get(Source,Config,default); Assert.Equal("unavailable",result.Status); Assert.Null(result.Title);
    }
    [Fact] public async Task CancellationPropagatesAndCleansPrivateCookieSnapshot()
    {
        await using var i = new TestInstance(); Directory.CreateDirectory(i.Paths.Get("profiles","facebook")); File.WriteAllText(i.Paths.Get("profiles","facebook","cookies.txt"),"synthetic cookie");
        using var cancel = new CancellationTokenSource(); string? copy = null;
        var tool = new Tool(async(s,c,ct) => { copy=c; Assert.NotNull(c); Assert.Equal("synthetic cookie",File.ReadAllText(c)); cancel.Cancel(); await Task.Delay(Timeout.Infinite,ct);return ""; });
        using var provider = new VideoContextProvider(i.Paths,tool:tool);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>provider.Get(new("https://www.facebook.com/person/videos/123/","post","facebook"),Config,cancel.Token));
        Assert.False(File.Exists(copy)); Assert.Empty(Directory.GetFiles(i.Paths.Get("video-context"),"*.json"));
    }
    [Fact] public async Task MissingToolIsCachedAndOversizedResponsesAreRejected()
    {
        await using var i = new TestInstance();var tool=new Tool((s,c,ct)=>throw new System.ComponentModel.Win32Exception());
        using var provider=new VideoContextProvider(i.Paths,tool:tool);
        Assert.Equal("tool_missing",(await provider.Get(Source,Config,default)).Error);await provider.Get(Source,Config,default);Assert.Equal(1,tool.Calls);
        await Assert.ThrowsAsync<IOException>(()=>VideoContextProvider.ReadBounded(new MemoryStream(new byte[11]),10,default));
    }
    sealed class Context(VideoContext result) : IVideoContextProvider
    { public int Calls; public Task<VideoContext> Get(VideoSource s,VideoContextConfig c,CancellationToken ct){Calls++;return Task.FromResult(result);} }
    sealed class CallbackContext(Func<CancellationToken,Task<VideoContext>> callback) : IVideoContextProvider
    { public Task<VideoContext> Get(VideoSource s,VideoContextConfig c,CancellationToken ct)=>callback(ct); }
    [Fact] public async Task EnrichmentTimeoutIsIncompleteEvidenceNotCancellationOfJudgment()
    {
        await using var i=new TestInstance();var tool=new Tool(async(s,c,ct)=>{await Task.Delay(Timeout.Infinite,ct);return "";});
        using var provider=new VideoContextProvider(i.Paths,tool:tool);
        var result=await provider.Get(Source,Config with{TimeoutSeconds=5},default);
        Assert.Equal("unavailable",result.Status);Assert.Equal("timeout",result.Error);
    }
    [Theory] [InlineData(false,false)] [InlineData(true,true)]
    public async Task DisabledEnrichmentAndDeterministicallyHiddenPostsDoNotFetch(bool enabled,bool hidden)
    {
        await using var i=new TestInstance();await i.Init();long id;
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="synthetic",SharedUrl=Source.Url,IngestReadyAt=Clock.Now};if(hidden)p.SetHidden("keyword","synthetic");db.Add(p);await db.SaveChangesAsync();id=p.Id;}
        var context=new Context(new(Source.Url,"shared","available"));
        using var http=new HttpClient(new Handler(r=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"content":"{\"score\":9,\"reason\":\"Allowed\",\"categories\":[]}"}}]}""")})));
        var media=new MediaFiles(i.Paths,http);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),VideoContext=Config with{Enabled=enabled},Llm=new(){Enabled=true,BaseUrl="http://synthetic.test/v1",Vision=false}},new(),Preferences.Parse(""));
        await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media),context).Run(snapshot,new("rescore",PostId:id),null,default);
        Assert.Equal(0,context.Calls);
    }
    [Fact] public async Task ChangeDuringEnrichmentPreventsModelDispatch()
    {
        await using var i=new TestInstance();await i.Init();long id;
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="synthetic",SharedUrl=Source.Url,IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();id=p.Id;}
        var context=new CallbackContext(async ct=>{await using var db=i.Factory.Open();await db.Posts.Where(p=>p.Id==id).ExecuteUpdateAsync(u=>u.SetProperty(p=>p.ContentRevision,p=>p.ContentRevision+1),ct);return new(Source.Url,"shared","available");});
        int calls=0;using var http=new HttpClient(new Handler(r=>{calls++;throw new InvalidOperationException("Must not call model");}));
        var media=new MediaFiles(i.Paths,http);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),VideoContext=Config,Llm=new(){Enabled=true,BaseUrl="http://synthetic.test/v1",Vision=false}},new(),Preferences.Parse(""));
        var counts=await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media),context).Run(snapshot,new("rescore",PostId:id),null,default);
        Assert.Equal(0,calls);Assert.Equal(1,counts.Superseded);
    }
    [Fact] public async Task SlowVideoEvidenceDoesNotBlockOtherPreparedModelWork()
    {
        await using var i=new TestInstance();await i.Init();
        await using(var db=i.Factory.Open()){db.Add(new Post{Platform="facebook",PlatformPostId="video",SharedUrl=Source.Url,PostedAt=Clock.Now,IngestReadyAt=Clock.Now});db.Add(new Post{Platform="facebook",PlatformPostId="text",PostedAt=Clock.Now.AddMinutes(-1),IngestReadyAt=Clock.Now});await db.SaveChangesAsync();}
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var modelCalled=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context=new CallbackContext(async ct=>{entered.SetResult();await release.Task.WaitAsync(ct);return new(Source.Url,"shared","available");});
        using var http=new HttpClient(new Handler(r=>{modelCalled.TrySetResult();return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"content":"{\"score\":9,\"reason\":\"Allowed\",\"categories\":[]}"}}]}""")});}));
        var media=new MediaFiles(i.Paths,http);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),VideoContext=Config,Llm=new(){Enabled=true,Parallel=2,BaseUrl="http://synthetic.test/v1",Vision=false}},new(),Preferences.Parse(""));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run=new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media),context).Run(snapshot,new("rescore"),null,timeout.Token);
        try{await entered.Task.WaitAsync(timeout.Token);await modelCalled.Task.WaitAsync(timeout.Token);Assert.False(run.IsCompleted);}finally{release.TrySetResult();}
        Assert.Equal(2,(await run).Completed);
    }
    [Theory]
    [InlineData("{\"video_context\":{\"timeout_seconds\":4}}")]
    [InlineData("{\"video_context\":{\"caption_languages\":[]}}")]
    [InlineData("{\"video_context\":{\"caption_languages\":[\".*\"]}}")]
    public void InvalidEnrichmentSettingsAreRejected(string json)
        => Assert.Throws<FormatException>(()=>InstanceValidation.Validate(InstanceValidation.Parse<FeedConfig>(json),new()));
    [Fact] public async Task ModelFailureGateIsRecheckedAfterSlowEnrichment()
    {
        await using var i=new TestInstance();await i.Init();
        await using(var db=i.Factory.Open()){db.Add(new Post{Platform="facebook",PlatformPostId="video",SharedUrl=Source.Url,PostedAt=Clock.Now,IngestReadyAt=Clock.Now});db.Add(new Post{Platform="facebook",PlatformPostId="text",PostedAt=Clock.Now.AddMinutes(-1),IngestReadyAt=Clock.Now});await db.SaveChangesAsync();}
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var context=new CallbackContext(async ct=>{await release.Task.WaitAsync(ct);return new(Source.Url,"shared","available");});
        int calls=0;using var http=new HttpClient(new Handler(r=>{Interlocked.Increment(ref calls);return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));}));
        var media=new MediaFiles(i.Paths,http);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),VideoContext=Config,Llm=new(){Enabled=true,Parallel=2,Batch=1,BaseUrl="http://synthetic.test/v1",Vision=false}},new(),Preferences.Parse(""));
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var counts=await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media),context).Run(snapshot,new("rescore"),s=>{if(s.StartsWith("batch #"))release.TrySetResult();},timeout.Token);
        Assert.Equal(1,calls);Assert.Equal(1,counts.Undispatched);Assert.Equal(0,counts.Enriching);
    }
    [Theory] [InlineData("available")] [InlineData("unavailable")]
    public async Task JudgmentUsesVideoEvidenceAndHashesExactEnrichedRequestWithoutHidingOnFetchFailure(string status)
    {
        await using var i=new TestInstance();await i.Init();long id;
        await using(var db=i.Factory.Open()){var p=new Post{Platform="facebook",PlatformPostId="synthetic",Text="Look!",SharedUrl=Source.Url,IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();id=p.Id;}
        var context=new Context(new(Source.Url,"shared",status,Uploader:"Other creator",Captions:new(status,Text:status=="available"?"Synthetic spoken content":null)));
        string? hash=null;
        using var http=new HttpClient(new Handler(async r=>{var body=await r.Content!.ReadAsStringAsync();hash=Prompts.Sha(body);using var json=JsonDocument.Parse(body);var text=json.RootElement.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("text").GetString()!;Assert.Contains("Video context:",text);Assert.Contains("Other creator",text);Assert.Contains(status,text);return new(HttpStatusCode.OK){Content=new StringContent("""{"choices":[{"message":{"content":"{\"score\":9,\"reason\":\"Allowed synthetic update\",\"categories\":[]}"}}]}""")};}));
        var media=new MediaFiles(i.Paths,http);var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),VideoContext=Config,Llm=new(){Enabled=true,BaseUrl="http://synthetic.test/v1",Vision=false}},new(),Preferences.Parse(""));
        var counts=await new Processing(i.Paths,i.Factory,new(http),media,new(i.Paths,i.Factory,media),context).Run(snapshot,new("rescore",PostId:id),null,default);
        Assert.Equal(1,counts.Completed);Assert.Equal(1,context.Calls);await using var check=i.Factory.Open();var stored=await check.Posts.SingleAsync();Assert.False(stored.Hidden);Assert.Equal(hash,stored.VerdictInputHash);
        Assert.NotEqual(Prompts.ConfigurationHash(snapshot),Prompts.ConfigurationHash(snapshot with{Config=snapshot.Config with{VideoContext=new()}}));
    }
}
