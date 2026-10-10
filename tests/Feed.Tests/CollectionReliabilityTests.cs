using System.Collections.Immutable;
using System.Net;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;

public sealed class CollectionReliabilityTests
{
    [Theory]
    [InlineData("clean",true)] [InlineData("different-person",false)] [InlineData("different-platform",false)]
    [InlineData("pending-ingest",false)] [InlineData("failed-ingest",false)] [InlineData("no-posts",false)]
    [InlineData("empty",false)] [InlineData("running",false)] [InlineData("no-association",false)]
    public async Task OnlyLaterCleanSamePersonVisitSupersedesCapture(string scenario,bool dismissed)
    {
        await using var i=new TestInstance();await i.Init();await using var db=i.Factory.Open();var now=Clock.Now;
        db.Runs.AddRange(new Run{Id=1,Kind="collect",Status="error",FinishedAt=now.AddHours(-2)},new Run{Id=2,Kind="collect",Status=scenario=="running"?"running":"ok",FinishedAt=scenario=="running"?null:now});
        db.TimelineVisits.AddRange(new TimelineVisit{Id=1,Platform="facebook",AuthorId=10,Url="https://www.facebook.com/10",RunId=1,CaptureToken="old",At=now.AddHours(-2)},
            new TimelineVisit{Id=2,Platform=scenario=="different-platform"?"instagram":"facebook",AuthorId=scenario=="different-person"?20:10,Url="https://www.facebook.com/10",RunId=2,CaptureToken="new",At=now,Status="rendered",CaptureStatus=scenario=="empty"?"empty":"captured",PostsFound=scenario=="no-posts"?0:3});
        db.RawSnapshots.AddRange(new RawSnapshot{Id=1,Platform="facebook",RunId=1,Path="old.json",CaptureToken=scenario=="no-association"?null:"old",Error="unknown",BlockedParserVersion=PayloadParser.Version},
            new RawSnapshot{Id=2,Platform="facebook",RunId=2,Path="new.json",CaptureToken="new",Parsed=true});
        if(scenario is "pending-ingest" or "failed-ingest")db.RawSnapshots.Add(new(){Id=3,Platform="facebook",RunId=2,Path="pending.json",CaptureToken="new",Error=scenario=="failed-ingest"?"error":null});
        await db.SaveChangesAsync();
        Assert.Equal(dismissed,await CaptureHealth.Superseded(db).AnyAsync(r=>r.Id==1));
        var s=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new())},new(),Preferences.Parse(""));
        Assert.Equal(!dismissed,(await RecoveryQuery.Read(db,s)).Any(r=>r.RawId==1));
        Assert.False((await db.RawSnapshots.FindAsync(1L))!.Parsed);Assert.Equal("incomplete",(await db.TimelineVisits.FindAsync(1L))!.CaptureStatus);
        // A new recurrence is independently visible; prior historical alerts stay superseded.
        db.RawSnapshots.Add(new(){Platform="facebook",RunId=2,Path="recurrence.json",Error="new error",BlockedParserVersion=PayloadParser.Version});await db.SaveChangesAsync();
        Assert.Contains(await RecoveryQuery.Read(db,s),r=>r.Problem=="new error" && r.Disposition==RecoveryDisposition.Compatibility);
    }
    [Fact] public void EvidenceChoicesAreExactEscapedAndRootScoped()
    {
        var choices=RecoveryEvidence.Choices("{\"a/b~\":true}\n{\"other\":1}");
        var a=Assert.Single(choices,c=>c.Path=="/a~1b~0");
        var p=RecoveryEvidence.Resolve(new([new(0,"auxiliary","reason",[a.Id])]),choices);
        Assert.Equal("/a~1b~0",p.Decisions[0].Evidence[0]);
        Assert.Throws<FormatException>(()=>RecoveryEvidence.Resolve(new([new(1,"auxiliary","reason",[a.Id])]),choices));
        Assert.Throws<FormatException>(()=>RecoveryEvidence.Resolve(new([new(0,"auxiliary","reason",["/a~1b~0"])]),choices));
        Assert.Throws<FormatException>(()=>RecoveryEvidence.Resolve(new([new(0,"auxiliary","reason",["#99999"])]),choices));
    }
    [Fact] public void JsonWrapperDoesNotSalvageProseOrTrailingData()
    {
        Assert.Equal("{}", RecoveryEvidence.Json("```json\n{}\n```"));
        Assert.ThrowsAny<JsonException>(()=>JsonDocument.Parse(RecoveryEvidence.Json("Explanation: {}")));
        Assert.ThrowsAny<JsonException>(()=>JsonDocument.Parse(RecoveryEvidence.Json("```json\n{}\n```\nextra")));
    }
    sealed class VisualHandler(string response,HttpStatusCode status=HttpStatusCode.OK):HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){Calls++;return Task.FromResult(new HttpResponseMessage(status){Content=new StringContent(JsonSerializer.Serialize(new{choices=new[]{new{finish_reason="stop",message=new{content=response}}}}))});}
    }
    [Theory][InlineData("posts_visible")][InlineData("empty_message_visible")][InlineData("login_or_checkpoint")]
    public async Task ScreenshotVerdictNeverChangesPostsCoverageOrLogin(string decision)
    {
        await using var i=new TestInstance();await i.Init();await using var db=i.Factory.Open();
        var file=i.Paths.Get("media","diagnostic.png");Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllBytesAsync(file,Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jRZkAAAAASUVORK5CYII="));
        db.Runs.Add(new(){Id=1,Kind="collect",Status="error",FinishedAt=Clock.Now});db.TimelineVisits.Add(new(){Id=1,RunId=1,Platform="facebook",Screenshot="media/diagnostic.png"});await db.SaveChangesAsync();
        var s=new InstanceSnapshot(new(){Llm=new(){Enabled=true,BaseUrl="http://model.example.test/v1"},CaptureRecovery=new(){Mode="apply",VisualDiagnosis=true}},new(),Preferences.Parse(""));
        using var handler=new VisualHandler("```json\n"+JsonSerializer.Serialize(new VisualDiagnosis(decision,"Visible evidence"))+"\n```");using var http=new HttpClient(handler);
        Assert.Equal(1,await new VisitDiagnosis(i.Paths,i.Factory,new(http)).Run(s,["facebook"],1,default));
        Assert.Equal(0,await new VisitDiagnosis(i.Paths,i.Factory,new(http)).Run(s,["facebook"],1,default));
        await db.Entry(await db.TimelineVisits.SingleAsync()).ReloadAsync();var v=await db.TimelineVisits.SingleAsync();
        Assert.Contains(decision,v.VisualDiagnosisJson);Assert.Equal("incomplete",v.CaptureStatus);Assert.Empty(await db.Posts.ToArrayAsync());Assert.Empty(await db.PlatformStates.ToArrayAsync());Assert.Equal(1,handler.Calls);
    }
}
