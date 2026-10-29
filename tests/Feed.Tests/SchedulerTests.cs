using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Feed.Tests;
public sealed class SchedulerTests
{
    [Fact] public async Task QueuedCollectShowsCooldownCountdownAndStartNowSkipsIt()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":true,"manual_cooldown_minutes":90},"backgrounds":{"source":"off"}}""");
        await using (var db = i.Factory.Open()) { db.Add(new RunRequest { Platform = "facebook", Mode = "home" }); db.Add(new PlatformState { Platform = "facebook", LastRunFinishedAt = Clock.Now.AddMinutes(-30) }); await db.SaveChangesAsync(); await db.Put("maintenance:last", Clock.Now.ToString("yyyy-MM-dd")); }
        var fake = i.Paths.Get("fake-cli"); await File.WriteAllTextAsync(fake, "#!/bin/sh\nexit 0\n"); File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var processing = ResourceLock.Try(i.Paths, "processing");
        var files = new InstanceFiles(i.Paths); var scheduler = new Scheduler(i.Paths, files, i.Factory, NullLogger<Scheduler>.Instance, () => fake);
        await scheduler.Tick(default);
        await using (var db = i.Factory.Open()) { var r = await db.RunRequests.SingleAsync(); Assert.Equal("pending", r.Status); Assert.StartsWith(Scheduler.CooldownPrefix + "60 min (at ", r.Note); }
        var management = new Management(i.Paths, i.Factory, new ManagementFiles(i.Paths));
        Assert.Contains("skipped", await management.Queue("facebook", "now", null, default));
        Assert.Contains("No queued", await management.Queue("facebook", "now", null, default));
        await scheduler.Tick(default);
        await using (var db = i.Factory.Open()) { var r = await db.RunRequests.SingleAsync(); Assert.True(r.SkipCooldown); Assert.NotEqual("pending", r.Status); }
    }
    [Fact] public async Task MissingCliFailsVisiblyAndRestartHonorsInstanceBackoff()
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"),"{\"platforms\":{\"facebook\":{}},\"backgrounds\":{\"source\":\"off\"}}");
        var files = new InstanceFiles(i.Paths); var scheduler = new Scheduler(i.Paths,files,i.Factory,NullLogger<Scheduler>.Instance,()=>i.Paths.Get("missing-cli"));
        await Assert.ThrowsAsync<FileNotFoundException>(()=>scheduler.Tick(default));
        await using (var db=i.Factory.Open()) { var r=await db.RunRequests.SingleAsync(); Assert.Equal("refused",r.Status); Assert.Contains("startup failed",r.Note); Assert.NotNull(await db.Get("process:not_before")); }
        var restarted = new Scheduler(i.Paths,files,i.Factory,NullLogger<Scheduler>.Instance,()=>throw new Exception("must not launch during backoff")); await restarted.Tick(default);
        await using var check=i.Factory.Open(); Assert.Equal(1,await check.RunRequests.CountAsync());
    }
    [Fact] public async Task DisabledTickRecoversClaimsAndValidEditReenablesScheduling()
    {
        await using var i=new TestInstance();await i.Init();
        var file=i.Paths.Get("config.json");await File.WriteAllTextAsync(file,"{\"platforms\":{\"instagram\":{}},\"scheduler\":{\"enabled\":false}}");
        await using(var db=i.Factory.Open()){db.RunRequests.Add(new(){Kind="process",Status="claimed",ClaimToken="stale",ClaimedAt=Clock.Now.AddMinutes(-3)});await db.SaveChangesAsync();}
        var scheduler=new Scheduler(i.Paths,new(i.Paths),i.Factory,NullLogger<Scheduler>.Instance,()=>i.Paths.Get("missing-cli"));
        // A paused tick still launches a queued process request, so the held processing lock keeps the recovered request waiting.
        using(var processing=ResourceLock.Try(i.Paths,"processing")){await scheduler.Tick(default);}
        await using(var db=i.Factory.Open()){Assert.Equal("pending",(await db.RunRequests.SingleAsync()).Status);}
        await File.WriteAllTextAsync(file,"{\"platforms\":{\"instagram\":{}},\"scheduler\":{\"enabled\":true}}");await Assert.ThrowsAsync<FileNotFoundException>(()=>scheduler.Tick(default));
    }
    [Fact] public async Task PausedTickStillDispatchesOwnerQueuedCollect()
    {
        await using var i=new TestInstance();await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"),"""{"platforms":{"facebook":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}""");
        await using(var db=i.Factory.Open()){Assert.Equal(1,await Actions.EnsureRequest(db,"collect","facebook","home"));}
        var scheduler=new Scheduler(i.Paths,new(i.Paths),i.Factory,NullLogger<Scheduler>.Instance,()=>i.Paths.Get("missing-cli"));
        await Assert.ThrowsAsync<FileNotFoundException>(()=>scheduler.Tick(default));
        await using var check=i.Factory.Open();var request=await check.RunRequests.SingleAsync();Assert.Equal("refused",request.Status);Assert.Contains("startup failed",request.Note);
    }
    [Fact] public async Task ProcessExitingBeforeRegistrationDoesNotLoopOnStartupGrace()
    {
        if(!OperatingSystem.IsLinux())return;
        await using var i=new TestInstance();await i.Init();await File.WriteAllTextAsync(i.Paths.Get("config.json"),"{\"platforms\":{\"facebook\":{}}}");
        var scheduler=new Scheduler(i.Paths,new(i.Paths),i.Factory,NullLogger<Scheduler>.Instance,()=>"/bin/false"); await scheduler.Tick(default);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(true){await using var db=i.Factory.Open();var r=await db.RunRequests.SingleAsync(timeout.Token);if(r.Status=="refused"){Assert.Contains("before registering",r.Note);Assert.NotNull(await db.Get("process:not_before"));break;}await Task.Delay(10,timeout.Token);}
    }
    [Fact] public async Task CaptureRecoveryHonorsPauseReloginAndStartupBackoff()
    {
        await using var i = new TestInstance(); await i.Init();
        var config = i.Paths.Get("config.json");
        await File.WriteAllTextAsync(config, """{"platforms":{"facebook":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}""");
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", IsFriend = true }; db.Add(author); await db.SaveChangesAsync();
            db.Add(new SweepTarget { Platform = "facebook", AuthorId = author.Id, CycleId = "fixture", State = "retry", Attempts = 1, RetryAt = Clock.Now.AddMinutes(-1) });
            db.Add(new PlatformState { Platform = "facebook", NeedsRelogin = true }); await db.SaveChangesAsync(); await db.Put("sweep:facebook:cycle", "fixture");
        }
        using var processing = ResourceLock.Try(i.Paths, "processing");
        Scheduler Scheduler() => new(i.Paths, new(i.Paths), i.Factory, NullLogger<Scheduler>.Instance, () => i.Paths.Get("missing-cli"));
        await Scheduler().Tick(default);
        await File.WriteAllTextAsync(config, """{"platforms":{"facebook":{}},"scheduler":{"enabled":true},"backgrounds":{"source":"off"}}""");
        await Scheduler().Tick(default);
        await using (var db = i.Factory.Open()) { Assert.Empty(await db.RunRequests.ToArrayAsync()); await db.PlatformStates.ExecuteUpdateAsync(u => u.SetProperty(p => p.NeedsRelogin, false)); }
        await Assert.ThrowsAsync<FileNotFoundException>(() => Scheduler().Tick(default));
        await Scheduler().Tick(default);
        await using var check = i.Factory.Open(); var request = await check.RunRequests.SingleAsync(); Assert.True(request.RetryIncomplete); Assert.Equal("refused", request.Status); Assert.NotNull(await check.Get("coverage:facebook:not_before"));
    }

}
