using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class CoverageTests
{
    [Fact]
    public async Task FailedVisitsAdvanceAndNewFriendsWaitForNextCycle()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open()) { for (int n = 0; n < 5; n++) db.Authors.Add(new() { Platform = "instagram", DisplayName = "Fixture " + n, IsFriend = true }); await db.SaveChangesAsync(); }
        var service = new TimelineCoverage(i.Factory);
        var first = await service.Select("instagram", 2); Assert.Equal(2, first.Count);
        foreach (var target in first) await service.Complete(new() { Platform = "instagram", AuthorId = target.AuthorId, Status = "unrendered" }, target);
        await using (var db = i.Factory.Open()) await db.SweepTargets.Where(t => t.State == "retry").ExecuteUpdateAsync(u => u.SetProperty(t => t.RetryAt, Clock.Now.AddSeconds(-1)));
        Assert.Empty(await service.Select("instagram", 2, retriesOnly: true)); // first pass precedes recovery
        await using (var db = i.Factory.Open()) await db.SweepTargets.Where(t => t.State == "retry").ExecuteUpdateAsync(u => u.SetProperty(t => t.RetryAt, Clock.Now.AddHours(1)));
        var second = await service.Select("instagram", 2); Assert.DoesNotContain(second, t => first.Any(f => f.AuthorId == t.AuthorId));
        await using (var db = i.Factory.Open()) { db.Authors.Add(new() { Platform = "instagram", DisplayName = "New fixture", IsFriend = true }); await db.SaveChangesAsync(); }
        foreach (var target in second) await service.Complete(new() { Platform = "instagram", AuthorId = target.AuthorId, Status = "empty", CaptureStatus = "empty" }, target);
        var last = Assert.Single(await service.Select("instagram", 2)); Assert.Equal(first[0].CycleId, last.CycleId);
        await service.Complete(new() { Platform = "instagram", AuthorId = last.AuthorId, Status = "checkpoint" }, last);
        Assert.Empty(await service.Select("instagram", 10)); // delayed retries keep this cycle open
        await using (var db = i.Factory.Open()) await db.SweepTargets.Where(t => t.State == "retry").ExecuteUpdateAsync(u => u.SetProperty(t => t.RetryAt, Clock.Now.AddSeconds(-1)));
        var retries = await service.Select("instagram", 10, retriesOnly: true); Assert.Equal(2, retries.Count);
        foreach (var target in retries) await service.Complete(new() { Platform = "instagram", AuthorId = target.AuthorId, Status = "rendered", CaptureStatus = "captured" }, target);
        var next = await service.Select("instagram", 10); Assert.Equal(6, next.Count); Assert.NotEqual(first[0].CycleId, next[0].CycleId);
        await using var check = i.Factory.Open(); Assert.Equal(5, await check.SweepTargets.CountAsync(t => t.VisitId != null && t.State == "done"));
    }
    [Fact]
    public async Task DeadInterruptedTargetBecomesPendingButLiveIngestDoesNot()
    {
        await using var i = new TestInstance(); await i.Init(); var ledger = new RunLedger(i.Factory);
        var run = await ledger.Start("collect", "facebook");
        await using (var db = i.Factory.Open()) { db.Authors.Add(new() { Platform = "facebook", IsFriend = true }); await db.SaveChangesAsync(); }
        var service = new TimelineCoverage(i.Factory); var target = Assert.Single(await service.Select("facebook", 1));
        await using (var db = i.Factory.Open()) { await db.SweepTargets.ExecuteUpdateAsync(u => u.SetProperty(t => t.State, "visiting").SetProperty(t => t.RunId, run.Id)); }
        await ledger.Recover(new()); Assert.Empty(await service.Select("facebook", 1));
        await ledger.Finish(run, "cancelled", 130); await ledger.Recover(new());
        Assert.Equal(target.AuthorId, Assert.Single(await service.Select("facebook", 1)).AuthorId);
    }
}
