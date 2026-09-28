using System.Diagnostics;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class RecoveryTests
{
    [Fact] public async Task ProcessIdentityDistinguishesLiveChildWrongStartAndExitedChild()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var child = Process.Start(new ProcessStartInfo("sleep", "30") { UseShellExecute = false })!;
        try
        {
            var started = ProcessIdentity.StartTime(child.Id);
            Assert.True(ProcessIdentity.Alive(child.Id, started));
            Assert.False(ProcessIdentity.Alive(child.Id, started.AddMilliseconds(-10)));
            await Task.Delay(30);
            Assert.Equal(started, ProcessIdentity.StartTime(child.Id));
            child.Kill(); await child.WaitForExitAsync();
            Assert.False(ProcessIdentity.Alive(child.Id, started));
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }
    [Fact]public async Task LocksAreExclusiveEvenInsideOneProcess(){await using var i=new TestInstance();using var first=ResourceLock.Try(i.Paths,"processing");Assert.NotNull(first);using var second=ResourceLock.Try(i.Paths,"processing");Assert.Null(second);Assert.True(ResourceLock.Busy(i.Paths,"processing"));}
    [Fact]public async Task StaleClaimCannotRegisterAndLiveWorkerSurvivesRecovery()
    {
        await using var i=new TestInstance();await i.Init();long id;await using(var db=i.Factory.Open()){var req=new RunRequest{Kind="process",Status="claimed",ClaimToken="new",ClaimedAt=Clock.Now};db.Add(req);await db.SaveChangesAsync();id=req.Id;}
        var ledger=new RunLedger(i.Factory);await Assert.ThrowsAsync<InvalidOperationException>(()=>ledger.Start("process",null,requestId:id,token:"old"));
        var run=await ledger.Start("process",null,requestId:id,token:"new");await using(var db=i.Factory.Open()){await db.Runs.Where(r=>r.Id==run.Id).ExecuteUpdateAsync(u=>u.SetProperty(r=>r.StartedAt,Clock.Now.AddMinutes(-10)).SetProperty(r=>r.Phase,"ingest"));}
        Assert.Empty(await ledger.Recover(new()));await ledger.Finish(run,"ok",0,token:"new");await using var check=i.Factory.Open();Assert.Equal("done",(await check.RunRequests.FindAsync(id))!.Status);
    }
    [Fact]public async Task DeadRunAndExpiredCollectRecoverButHeartRemains()
    {
        await using var i=new TestInstance();await i.Init();await using(var db=i.Factory.Open()){db.Runs.Add(new(){Kind="collect",Platform="facebook",ProcessPid=int.MaxValue,ProcessStartedAt=Clock.Now.AddHours(-2),StartedAt=Clock.Now.AddMinutes(-10)});db.RunRequests.AddRange(new(){Kind="collect",Platform="facebook",RequestedAt=Clock.Now.AddHours(-2)},new(){Kind="like",Platform="instagram",RequestedAt=Clock.Now.AddDays(-2)});await db.SaveChangesAsync();}
        Assert.Single(await new RunLedger(i.Factory).Recover(new()));await using var check=i.Factory.Open();Assert.Equal("expired",(await check.RunRequests.SingleAsync(r=>r.Kind=="collect")).Status);Assert.Equal("pending",(await check.RunRequests.SingleAsync(r=>r.Kind=="like")).Status);Assert.Equal("error",(await check.Runs.SingleAsync()).Status);
    }
    [Fact]public async Task MediaPathRejectsEscapingSymlink()
    {
        await using var i=new TestInstance();var secret=i.Paths.Get("config.json");await File.WriteAllTextAsync(secret,"private");File.CreateSymbolicLink(i.Paths.Get("media","escape.jpg"),secret);Assert.Null(i.Paths.SafeFile("media","escape.jpg"));Assert.Null(i.Paths.SafeFile("media","../config.json"));
    }
}
