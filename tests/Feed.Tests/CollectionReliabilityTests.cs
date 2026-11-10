using System.Collections.Immutable;
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
        Assert.Equal(!dismissed,await RecoveryQuery.HasIssues(db,s));
        Assert.False((await db.RawSnapshots.FindAsync(1L))!.Parsed);Assert.Equal("incomplete",(await db.TimelineVisits.FindAsync(1L))!.CaptureStatus);
        // A new recurrence is independently visible; prior historical alerts stay superseded.
        db.RawSnapshots.Add(new(){Platform="facebook",RunId=2,Path="recurrence.json",Error="new error",BlockedParserVersion=PayloadParser.Version});await db.SaveChangesAsync();
        Assert.Contains(await RecoveryQuery.Read(db,s),r=>r.Problem=="new error" && r.Disposition==RecoveryDisposition.Compatibility);
    }
    [Fact] public async Task ThreeSilentHomeCollectsRaiseAttentionUntilOneFindsPosts()
    {
        await using var i=new TestInstance();await i.Init();await using var db=i.Factory.Open();
        var s=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new())},new(),Preferences.Parse(""));
        static Run Collect(long id,int found,string mode="home",string status="ok",string scope="home")=>new(){Id=id,Platform="facebook",Kind="collect",Mode=mode,Status=status,PostsFound=found,FinishedAt=Clock.Now,StatsJson=$"{{\"scope\":\"{scope}\"}}"};
        db.Runs.AddRange(Collect(1,0),Collect(2,0));await db.SaveChangesAsync();
        Assert.Equal(2,(await CaptureHealth.SilentHomeCollects(db,"facebook")).Length);Assert.DoesNotContain(await RecoveryQuery.Read(db,s),r=>r.Item=="home collects");
        // A close-friends check, a failed browser run and a person collect (Mode "home", scope "person") are not
        // home collects: they neither count nor break the sequence.
        db.Runs.AddRange(Collect(3,0,"close_friends"),Collect(4,0,status:"error"),Collect(5,7,scope:"person"),Collect(6,0));await db.SaveChangesAsync();
        Assert.Equal(new long[]{6,2,1},await CaptureHealth.SilentHomeCollects(db,"facebook"));
        var issue=Assert.Single(await RecoveryQuery.Read(db,s),r=>r.Item=="home collects");Assert.Equal(RecoveryDisposition.Attention,issue.Disposition);Assert.Equal(6,issue.RunId);Assert.Equal(DiagnosticLevel.Alert,await RecoveryQuery.Level(db,s));
        db.Runs.Add(Collect(7,4));await db.SaveChangesAsync();
        Assert.Empty(await CaptureHealth.SilentHomeCollects(db,"facebook"));Assert.DoesNotContain(await RecoveryQuery.Read(db,s),r=>r.Item=="home collects");
    }
}
