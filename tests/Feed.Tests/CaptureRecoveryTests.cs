using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Xunit;
using Microsoft.EntityFrameworkCore;
namespace Feed.Tests;

public sealed class CaptureRecoveryTests
{
    const string Auxiliary = """{"label":"ProfileCometProfilePicture_user$defer$ProfileCometNewPictureMenu_user","path":["user","profile_header_renderer","user"],"data":{"story_bucket":{"nodes":[]},"cosmetic_hint":true}}""";
    static RecoveryProposal AuxProposal => new([new(0,"auxiliary","Only profile menu metadata",["/data/story_bucket"])]);
    static InstanceSnapshot Snapshot(string mode = "apply") => new(new() { Platforms = ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()), Llm = new() { Enabled = true, BaseUrl = "http://model.example.test/v1", Model = "synthetic" }, CaptureRecovery = new() { Mode = mode } }, new(), Preferences.Parse(""));
    sealed class Handler(string response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls; public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++; Body = await request.Content!.ReadAsStringAsync(ct);
            var reply = response;
            // Test fixtures use source paths; emulate the model selecting supplied IDs.
            if (response.Contains("Decisions")) {
                using var requestDoc=JsonDocument.Parse(Body);
                var input=JsonDocument.Parse(requestDoc.RootElement.GetProperty("messages")[1].GetProperty("content").GetString()!);
                var choices=JsonSerializer.Deserialize<EvidenceChoice[]>(input.RootElement.GetProperty("evidence_choices").GetRawText())!;
                var proposal=JsonSerializer.Deserialize<RecoveryProposal>(response)!;
                reply=JsonSerializer.Serialize(new RecoveryProposal(proposal.Decisions.Select(d=>d with {Evidence=d.Evidence.Select(p=>choices.Single(c=>c.Root==d.Root && c.Path==p).Id).ToArray(),Fields=d.Fields?.ToDictionary(f=>f.Key,f=>choices.Single(c=>c.Root==d.Root && c.Path==f.Value).Id)}).ToArray()));
            }
            return new(status) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = "stop", message = new { content = reply } } } })) }; }
    }
    static async Task<string> Raw(TestInstance i, string body, string name = "001.jsonl")
    { var path = i.Paths.Get("raw","facebook","capture",name); Directory.CreateDirectory(Path.GetDirectoryName(path)!); await File.WriteAllTextAsync(path,body); return path; }
    [Fact] public void UnknownJsonlSiblingRemainsDiagnosticBesideRecognizedContent()
    {
        const string good = """{"data":{"story":{"__typename":"Story","post_id":"100","creation_time":1700000000,"actors":[{"id":"200","name":"Synthetic"}],"message":{"text":"Hello"}}}}""";
        var p = PayloadParser.Parse("facebook",good + "\n{\"data\":{\"unknown_feed\":{}}}");
        Assert.Single(p.Posts); Assert.NotEmpty(p.Diagnostics); Assert.Contains(p.Issues,x=>x.Root==1 && x.Code=="unknown_response");
    }
    [Fact] public async Task SidecarSurvivesDiscoveryAndMismatchCannotClearFailure()
    {
        await using var i = new TestInstance(); await i.Init(); var path = await Raw(i,Auxiliary);
        var context = new CaptureContext(1,"network","SyntheticFeed","/api/graphql/",200,"application/json","visit-fixture","");
        CaptureContext.Publish(path,Auxiliary,context); Assert.Equal("visit-fixture",CaptureContext.Read(path,Auxiliary)!.Visit);
        using var http = new HttpClient(new Handler("{}")); var ingest = new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        await ingest.Discover("facebook",default); await ingest.File("facebook",path,Snapshot(),true,default);
        await using (var db=i.Factory.Open()) { var r=await db.RawSnapshots.SingleAsync(); Assert.Contains("SyntheticFeed",r.ContextJson); Assert.NotEmpty(await db.RawDiagnostics.ToListAsync()); }
        await File.WriteAllTextAsync(path,Auxiliary+" "); Assert.Throws<FormatException>(()=>CaptureContext.Read(path,Auxiliary+" "));
        Assert.Equal(1,(await ingest.File("facebook",path,Snapshot(),true,default)).Failed);
        File.Delete(path+".context"); Assert.Null(CaptureContext.Read(path,Auxiliary+" "));
    }
    [Fact] public void RecoveryValidatesEvidenceAndNeverClaimsAnEmptyTimeline()
    {
        Assert.NotEmpty(PayloadParser.Parse("facebook",Auxiliary).Diagnostics);
        var p = CaptureRecovery.Validate("facebook",Auxiliary,AuxProposal); Assert.Empty(p.Diagnostics); Assert.Empty(p.Posts); Assert.False(p.ExplicitEmpty);
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",Auxiliary,new([new(0,"auxiliary","x",["/missing"])])));
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",Auxiliary,new([new(0,"empty","x",["/data"])])));
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",Auxiliary,new([])));
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",Auxiliary.Replace("story_bucket","attachments"),new([new(0,"auxiliary","x",["/data/attachments"])])));
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",Auxiliary.Replace("{\"label\"","{\"errors\":[{\"message\":\"content failed\"}],\"label\""),AuxProposal));
    }
    [Theory][InlineData("observe",false)][InlineData("apply",true)]
    public async Task ModelRecoveryIsAuditedBoundedAndReplayable(string mode,bool applied)
    {
        await using var i=new TestInstance(); await i.Init(); var path=await Raw(i,Auxiliary); using var handler=new Handler(JsonSerializer.Serialize(AuxProposal)); using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http)); await ingest.File("facebook",path,Snapshot(mode),true,default);
        var worker=new CaptureRecovery(i.Paths,i.Factory,new(http),ingest);
        Assert.Equal(applied?1:0,await worker.Run(Snapshot(mode),["facebook"],2,default));
        Assert.Equal(0,await worker.Run(Snapshot(mode),["facebook"],2,default)); Assert.Equal(1,handler.Calls);
        Assert.Contains("cosmetic_hint",handler.Body); // Captured content is sent intact, not redacted.
        await using(var db=i.Factory.Open()) { Assert.Equal(applied,(await db.RawSnapshots.SingleAsync()).Parsed); var a=await db.CaptureRecoveryAttempts.SingleAsync(); Assert.Equal("accepted",a.Validation); Assert.NotNull(a.Response); Assert.NotEmpty(a.InputHash); Assert.Empty(await db.Posts.ToArrayAsync()); }
        Assert.Equal(Auxiliary,await File.ReadAllTextAsync(path));
        if(applied) { Assert.Equal(0,(await ingest.File("facebook",path,Snapshot(),true,default)).Failed); Assert.Equal(1,handler.Calls); }
    }
    [Fact] public async Task UnknownDecisionStaysVisibleAndDoesNotLoop()
    {
        await using var i=new TestInstance(); await i.Init();var path=await Raw(i,Auxiliary); using var handler=new Handler(JsonSerializer.Serialize(new RecoveryProposal([new(0,"unknown","Cannot determine",["/data"])])));using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));await ingest.File("facebook",path,Snapshot(),true,default); var worker=new CaptureRecovery(i.Paths,i.Factory,new(http),ingest);
        await worker.Run(Snapshot(),["facebook"],2,default);await worker.Run(Snapshot(),["facebook"],2,default);
        Assert.Equal(1,handler.Calls); await using var db=i.Factory.Open();Assert.False((await db.RawSnapshots.SingleAsync()).Parsed);Assert.Equal("attention",(await db.RawSnapshots.SingleAsync()).RecoveryState);
    }
    [Fact] public async Task TransportOutageSharesDelayWithoutExhaustingCaptureDecisionBudget()
    {
        await using var i=new TestInstance();await i.Init();var path=await Raw(i,Auxiliary);using var handler=new Handler("{}",HttpStatusCode.ServiceUnavailable);using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));await ingest.File("facebook",path,Snapshot(),true,default);var worker=new CaptureRecovery(i.Paths,i.Factory,new(http),ingest);
        await worker.Run(Snapshot(),["facebook"],2,default);await worker.Run(Snapshot(),["facebook"],2,default);Assert.Equal(1,handler.Calls);
        await using(var db=i.Factory.Open()) { await db.RawSnapshots.ExecuteUpdateAsync(x=>x.SetProperty(r=>r.RecoveryAttemptedAt,Clock.Now.AddHours(-2))); await db.Kv.Where(k=>k.Key.StartsWith("capture-model:")).ExecuteDeleteAsync(); }
        await worker.Run(Snapshot(),["facebook"],2,default);await worker.Run(Snapshot(),["facebook"],2,default);Assert.Equal(2,handler.Calls);
        await using(var db=i.Factory.Open()) { var r=await db.RawSnapshots.SingleAsync();Assert.Equal("retry",r.RecoveryState);Assert.Equal(0,r.RecoveryAttempts);Assert.Equal(2,await db.CaptureRecoveryAttempts.CountAsync()); }
    }
    [Fact] public async Task SameShapeDifferentContentNeverReusesDecision()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new Handler(JsonSerializer.Serialize(AuxProposal));using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        await ingest.File("facebook",await Raw(i,Auxiliary),Snapshot(),true,default);
        await ingest.File("facebook",await Raw(i,Auxiliary.Replace("true","false"),"002.jsonl"),Snapshot(),true,default);
        await new CaptureRecovery(i.Paths,i.Factory,new(http),ingest).Run(Snapshot(),["facebook"],2,default);Assert.Equal(2,handler.Calls);
    }
    [Fact] public async Task FingerprintsGroupRepeatedCapturesAndDetailsArePaged()
    {
        await using var i=new TestInstance();await i.Init();await using var db=i.Factory.Open();
        for(int n=0;n<70;n++){var raw=new RawSnapshot{Platform="facebook",Path=$"raw/{n}",Error="unknown",BlockedParserVersion=PayloadParser.Version};db.Add(raw);await db.SaveChangesAsync(); db.RawDiagnostics.Add(new(){RawSnapshotId=raw.Id,Code="unknown_response",Fingerprint="same",Impact="unknown_or_content",Evidence="unknown"});}
        await db.SaveChangesAsync();var s=Snapshot() with {Config=Snapshot().Config with {CaptureRecovery=new(){Mode="off"}}};
        var grouped=await RecoveryQuery.Read(db,s,grouped:true); Assert.Single(grouped);Assert.Equal(70,grouped[0].CaptureCount);
        Assert.Equal(50,(await RecoveryQuery.Read(db,s,fingerprint:"same",page:1)).Length);Assert.Equal(20,(await RecoveryQuery.Read(db,s,fingerprint:"same",page:2)).Length);
    }
    [Fact] public void FingerprintIgnoresValuesAndArrayIndexesButKeepsContentImpact()
    {
        using var a=JsonDocument.Parse("""{"data":{"items":[{"id":"100","text":"first"}]}}""");using var b=JsonDocument.Parse("""{"data":{"items":[{"id":"200","text":"second"}]}}""");
        var x=CaptureDiagnostics.Create("instagram","operation",a.RootElement,0,"instagram/GraphQL: first; path=items/0/caption",false);
        var y=CaptureDiagnostics.Create("instagram","operation",b.RootElement,0,"instagram/GraphQL: different; path=items/9/caption",false);
        Assert.Equal(x.Fingerprint,y.Fingerprint);Assert.NotEqual(x.Fingerprint,CaptureDiagnostics.Create("instagram","operation",a.RootElement,0,"instagram/GraphQL: first; path=items/0/caption",true).Fingerprint);
    }
    const string PostSource = """{"record":{"record_id":"101","by":{"key":"202","label":"Synthetic Person"},"body":"Recovered caption","link":"https://www.facebook.com/202/posts/101","time":1700000000,"flags":{"sponsored":false,"suggested":false,"reel":false,"event":false}}}""";
    static RecoveryProposal PostProposal => new([new(0,"post","A single evidenced text post",["/record"],new() { ["id"]="/record/record_id",["author_id"]="/record/by/key",["author_name"]="/record/by/label",["text"]="/record/body",["permalink"]="/record/link",["timestamp"]="/record/time",["sponsored"]="/record/flags/sponsored",["suggested"]="/record/flags/suggested",["reel"]="/record/flags/reel",["event"]="/record/flags/event" })]);
    [Fact] public void RecoveredPostNeedsGroundedIdentityAndExplicitPolicyFlags()
    {
        var p=Assert.Single(CaptureRecovery.Validate("facebook",PostSource,PostProposal).Posts);Assert.True(p.IsPartial);Assert.Equal("Recovered caption",p.Post.Text);Assert.Null(p.Post.LikeRef);
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",PostSource.Replace("posts/101","posts/999"),PostProposal));
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",PostSource.Replace("\"suggested\":false","\"suggested\":true"),PostProposal));
        var wrong=PostProposal.Decisions[0].Fields!;wrong["text"]="/missing";
        Assert.Throws<FormatException>(()=>CaptureRecovery.Validate("facebook",PostSource,new([PostProposal.Decisions[0] with {Fields=wrong}])));
    }
    [Fact] public async Task RecoveredPostsCannotOverwriteStoredContentAndAuditDoesNotWrite()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new Handler(JsonSerializer.Serialize(PostProposal));using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));var path=await Raw(i,PostSource);await ingest.File("facebook",path,Snapshot(),true,default);
        await using(var db=i.Factory.Open()){db.Posts.Add(new(){Platform="facebook",PlatformPostId="101",Text="Existing caption",ContentHash="fixture-hash",IngestReadyAt=Clock.Now,RawRef="other",ContentRevision=7});await db.SaveChangesAsync();}
        Assert.Equal(1,await new CaptureRecovery(i.Paths,i.Factory,new(http),ingest).Run(Snapshot(),["facebook"],2,default));
        await using(var db=i.Factory.Open()){var post=await db.Posts.SingleAsync();Assert.Equal("Existing caption",post.Text);Assert.Equal(7,post.ContentRevision);}
        async Task<string> State() { await using var db=i.Factory.Open(); return JsonSerializer.Serialize(new { Posts=await db.Posts.OrderBy(x=>x.Id).ToArrayAsync(), Raw=await db.RawSnapshots.OrderBy(x=>x.Id).ToArrayAsync(), Diagnostics=await db.RawDiagnostics.OrderBy(x=>x.Id).ToArrayAsync(), Attempts=await db.CaptureRecoveryAttempts.OrderBy(x=>x.Id).ToArrayAsync(), Runs=await db.Runs.OrderBy(x=>x.Id).ToArrayAsync() }); }
        // WAL checkpoints can change physical DB bytes when a read connection closes.
        // The read-only contract concerns logical records, not checkpoint placement.
        var before=await State();var report=await ParserAudit.Read(i.Factory,"facebook",5,null,null,false,default);Assert.Single(report);
        Assert.Equal(before,await State());Assert.Equal(1,handler.Calls);
    }
    [Theory][InlineData(1)][InlineData(2)][InlineData(3)] public async Task AppliedRecoveryOverlayIsBoundToImmutableSource(int version)
    {
        await using var i=new TestInstance();await i.Init();var path=await Raw(i,Auxiliary);
        File.WriteAllText(path+".recovery",JsonSerializer.Serialize(new RecoveryOverlay(version,CaptureContext.Hash(Auxiliary),AuxProposal)));
        Assert.Empty(CaptureRecovery.ParseSaved("facebook",Auxiliary,path).Diagnostics);
        Assert.Throws<FormatException>(()=>CaptureRecovery.ParseSaved("facebook",Auxiliary+" ",path));
    }
    [Fact] public async Task OffOrOversizedRecoveryMakesNoModelCall()
    {
        await using var i=new TestInstance();await i.Init();using var handler=new Handler("{}");using var http=new HttpClient(handler);var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        var path=await Raw(i,Auxiliary+new string(' ',2000));await ingest.File("facebook",path,Snapshot(),true,default);var worker=new CaptureRecovery(i.Paths,i.Factory,new(http),ingest);
        await worker.Run(Snapshot("off"),["facebook"],2,default);
        var s=Snapshot() with {Config=Snapshot().Config with {CaptureRecovery=new(){Mode="apply",MaxInputChars=1000}}};await worker.Run(s,["facebook"],2,default);Assert.Equal(0,handler.Calls);
    }
    [Fact] public void UnknownDataSiblingRemainsDiagnostic()
    {
        const string body="""{"data":{"story":{"__typename":"Story","post_id":"1","creation_time":1700000000,"actors":[{"id":"2","name":"Synthetic"}],"message":{"text":"Hello"}},"future_content":{"something":"unknown"}}}""";
        var parsed=PayloadParser.Parse("facebook",body);Assert.Single(parsed.Posts);Assert.Contains(parsed.Diagnostics,x=>x.Contains("unrecognized data branch"));
    }

    [Fact] public void StoriesTrayIsAuxiliaryButUnknownDiscoverContentRemainsDiagnostic()
    {
        const string body="""{"data":{"cat_sensitive":{"cat":"MULTI_FB_STORIES_TRAY","enc":null},"node":{"__typename":"DiscoverFeedUnit","content":{"item_objects":[{"id":"synthetic","data":{"__typename":"DirectMessageThreadBucket","__isStoryBucket":"DirectMessageThreadBucket"}}]}}}}""";
        var p=PayloadParser.Parse("facebook",body);Assert.Empty(p.Posts);Assert.Empty(p.Diagnostics);Assert.False(p.ExplicitEmpty);
        Assert.NotEmpty(PayloadParser.Parse("facebook",body.Replace("MULTI_FB_STORIES_TRAY","NEW_CONTENT")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook",body.Replace("\"content\":{","\"post_id\":\"123\",\"content\":{")).Diagnostics);
    }
    [Fact] public void EnvelopeMetadataCannotHideUnknownNestedContent()
    {
        const string body="""{"data":{"story":{"__typename":"Story","post_id":"1","creation_time":1700000000,"actors":[{"id":"2","name":"Synthetic"}],"message":{"text":"Hello"}},"feed_backend_data":{"qid":"synthetic","serialized_payloads":"opaque"}}}""";
        Assert.Empty(PayloadParser.Parse("facebook",body).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook",body.Replace("\"qid\":\"synthetic\"","\"new_content\":{}")).Diagnostics);
    }

    [Fact] public async Task LargeKnownSiblingDoesNotBlockSmallUnknownRootRecovery()
    {
        await using var i=new TestInstance(); await i.Init();
        var known = "{\"data\":{\"viewer\":{\"feed_comet_composer\":{}}}}" + new string(' ', 50000);
        var path=await Raw(i,known + "\n" + Auxiliary);
        using var handler=new Handler(JsonSerializer.Serialize(AuxProposal)); using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http)); var s=Snapshot() with {Config=Snapshot().Config with {CaptureRecovery=new(){Mode="apply",MaxInputChars=12000}}};
        await ingest.File("facebook",path,s,true,default);
        Assert.Equal(1,await new CaptureRecovery(i.Paths,i.Factory,new(http),ingest).Run(s,["facebook"],2,default));
        Assert.Equal(1,handler.Calls); Assert.DoesNotContain("feed_comet_composer",JsonDocument.Parse(handler.Body!).RootElement.GetProperty("messages")[1].GetProperty("content").GetString());
        await using var db=i.Factory.Open(); Assert.True((await db.RawSnapshots.SingleAsync()).Parsed);
        Assert.Equal(CaptureContext.Hash(known+"\n"+Auxiliary),(await db.CaptureRecoveryAttempts.SingleAsync()).BodyHash);
    }
    [Fact] public async Task ApplyingOneRootDoesNotClearAnOversizedUnknownSibling()
    {
        await using var i=new TestInstance(); await i.Init();
        var body="{\"data\":{\"unknown\":true}}" + new string(' ',50000) + "\n" + Auxiliary;
        var path=await Raw(i,body); using var handler=new Handler(JsonSerializer.Serialize(AuxProposal)); using var http=new HttpClient(handler);
        var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http)); var s=Snapshot() with {Config=Snapshot().Config with {CaptureRecovery=new(){Mode="apply",MaxInputChars=12000}}};
        await ingest.File("facebook",path,s,true,default);
        Assert.Equal(1,await new CaptureRecovery(i.Paths,i.Factory,new(http),ingest).Run(s,["facebook"],2,default));
        Assert.True(File.Exists(path+".recovery")); Assert.Single(CaptureRecovery.ParseSaved("facebook",body,path).Diagnostics);
        await using var db=i.Factory.Open(); var raw=await db.RawSnapshots.SingleAsync();
        Assert.False(raw.Parsed); Assert.Equal("retry",raw.RecoveryState); Assert.Equal("unresolved",(await db.CaptureRecoveryAttempts.SingleAsync()).Validation);
        Assert.Equal(body,await File.ReadAllTextAsync(path));
    }

    [Fact] public async Task PublishedOverlayResumesInterruptedIngestWithoutAnotherModelCall()
    {
        await using var i=new TestInstance(); await i.Init(); var path=await Raw(i,Auxiliary);
        using var handler=new Handler("{}"); using var http=new HttpClient(handler); var ingest=new Ingest(i.Paths,i.Factory,new(i.Paths,http));
        await ingest.File("facebook",path,Snapshot(),true,default);
        File.WriteAllText(path+".recovery",JsonSerializer.Serialize(new RecoveryOverlay(2,CaptureContext.Hash(Auxiliary),AuxProposal)));
        Assert.Equal(1,await new CaptureRecovery(i.Paths,i.Factory,new(http),ingest).Run(Snapshot(),["facebook"],2,default));
        Assert.Equal(0,handler.Calls); await using var db=i.Factory.Open(); Assert.True((await db.RawSnapshots.SingleAsync()).Parsed);
        Assert.Equal("applied",(await db.RawSnapshots.SingleAsync()).RecoveryState);
    }

}
