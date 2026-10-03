using System.Collections.Immutable;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Feed.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Feed.Tests;
public sealed class PersonTests
{
    static InstanceSnapshot Snapshot(bool enabled = true, bool scheduling = true) => new(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new() { Enabled = enabled }), Scheduler = new() { Enabled = scheduling }, Llm = new() { Enabled = true } }, new() { Categories = [new() { Key = "life", Label = "Life" }, new() { Key = "other", Label = "Other" }] }, Preferences.Parse(""));
    [Fact] public async Task PersonSelectionPrecedesCapAndIncludesUnjudgedWithoutLeakingOtherAuthorsOrHiddenPosts()
    {
        await using var i = new TestInstance(); await i.Init(); long id;
        await using (var db = i.Factory.Open())
        {
            var chosen = new Author { Platform = "facebook", DisplayName = "Synthetic Person", Url = "https://www.facebook.com/synthetic/", RefsJson = "[\"fb:synthetic\"]" };
            var other = new Author { Platform = "facebook", DisplayName = "Synthetic Other" }; db.AddRange(chosen, other); await db.SaveChangesAsync(); id = chosen.Id;
            Post Row(string key, long author, string? categories = "[\"life\"]", int revision = 1, bool hidden = false) => new() { Platform = "facebook", PlatformPostId = key, AuthorId = author, CategoriesJson = categories, VerdictContentRevision = revision, PostedAt = Clock.Now, Hidden = hidden };
            db.AddRange(Row("visible", id), Row("other-category", id, "[\"other\"]"), Row("pending", id, null), Row("stale", id, revision: 0), Row("hidden", id, hidden: true), Row("outsider", other.Id)); await db.SaveChangesAsync();
        }
        var people = new People(i.Factory); var s = Snapshot(enabled: false);
        var all = (await people.Read(id, s, null, "live", default))!;
        Assert.Equal(4, all.Feed.VisibleCount); Assert.Equal(1, all.Feed.HiddenCount); Assert.Equal(2, all.Feed.UnsortedCount);
        Assert.All(all.Feed.Items, u => Assert.Equal(id, u.Lead.Post.AuthorId));
        var filtered = (await people.Read(id, s, "life", "live", default))!;
        Assert.Equal("visible", Assert.Single(filtered.Feed.Items).Lead.Post.PlatformPostId);
        Assert.Equal("hidden", Assert.Single((await people.Read(id, s, null, "hidden", default))!.Feed.Items).Lead.Post.PlatformPostId);
        Assert.Equal(2, (await people.Read(id, s, null, "unsorted", default))!.Feed.Items.Length);
        Assert.Null(await people.Read(long.MaxValue, s, null, "live", default));
        await Assert.ThrowsAsync<ArgumentException>(() => people.Read(id, s, "invalid", "live", default));
        var capped = s with { Config = s.Config with { Ui = s.Config.Ui with { RenderCap = 1 } } };
        Assert.Equal("visible", Assert.Single((await people.Read(id, capped, "life", "live", default))!.Feed.Items).Lead.Post.PlatformPostId);
        await using var insert = i.Factory.Open(); var empty = new Author { Platform = "instagram", DisplayName = "Synthetic Empty" }; insert.Add(empty); await insert.SaveChangesAsync();
        Assert.Empty((await people.Read(empty.Id, s, null, "live", default))!.Feed.Items);
    }
    [Fact] public async Task PersonQueueHonorsGatesDeduplicatesAndBindsWorkerToExactTarget()
    {
        await using var i = new TestInstance(); await i.Init(); long id;
        await using (var db = i.Factory.Open()) { var author = new Author { Platform = "facebook", Url = "https://facebook.com/profile.php?id=123", PlatformAuthorId = "123" }; db.Add(author); await db.SaveChangesAsync(); id = author.Id; }
        var people = new People(i.Factory);
        Assert.Contains("paused", await people.Collect(id, Snapshot(scheduling: false), default));
        Assert.Contains("paused", await people.Collect(id, Snapshot(enabled: false), default));
        await using (var db = i.Factory.Open()) { db.Add(new PlatformState { Platform = "facebook", NeedsRelogin = true }); await db.SaveChangesAsync(); }
        Assert.Contains("login", await people.Collect(id, Snapshot(), default));
        await using (var db = i.Factory.Open()) await db.PlatformStates.ExecuteUpdateAsync(u => u.SetProperty(p => p.NeedsRelogin, false));
        Assert.Null(await people.Collect(id, Snapshot(), default));
        Assert.Contains("queued", await people.Collect(id, Snapshot(), default));
        long requestId; string target;
        await using (var db = i.Factory.Open()) {
            var request = await db.RunRequests.SingleAsync(); requestId = request.Id; target = request.Person!;
            Assert.Equal(id, request.PersonAuthorId); Assert.Equal("home", request.Mode); Assert.Equal("https://www.facebook.com/profile.php?id=123", target);
            request.Status = "claimed"; request.ClaimToken = "synthetic"; request.ClaimedAt = Clock.Now; await db.SaveChangesAsync();
        }
        var ledger = new RunLedger(i.Factory);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ledger.Start("collect", "facebook", "home", requestId: requestId, token: "synthetic"));
        var run = await ledger.Start("collect", "facebook", "home", requestId: requestId, token: "synthetic", person: target);
        await ledger.Finish(run, "ok", 0, token: "synthetic");
        var page = (await people.Read(id, Snapshot(), null, "live", default))!;
        Assert.Equal("done", page.Request!.Status); Assert.Equal("ok", page.Run!.Status);
        Assert.Null(People.ProfileUrl(new() { Platform = "instagram", PlatformAuthorId = "123" }));
        Assert.Null(People.ProfileUrl(new() { Platform = "facebook", Url = "https://example.test/synthetic" }));
    }
    [Fact] public async Task PersonRequestSurvivesRecoveryAndDispatchesBoundedArgumentsAfterCooldown()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":true},"backgrounds":{"source":"off"}}""");
        const string target = "https://www.facebook.com/profile.php?id=123";
        await using (var db = i.Factory.Open()) {
            db.Add(new RunRequest { Platform = "facebook", Mode = "home", Person = target, PersonAuthorId = 123, Status = "claimed", ClaimToken = "stale", ClaimedAt = Clock.Now.AddMinutes(-5) });
            db.Add(new PlatformState { Platform = "facebook", LastRunFinishedAt = Clock.Now }); await db.SaveChangesAsync(); await db.Put("maintenance:last", Clock.Now.ToString("yyyy-MM-dd"));
        }
        var fake = i.Paths.Get("fake-cli");
        await File.WriteAllTextAsync(fake, "#!/bin/sh\nprintf '%s\\n' \"$@\" > \"$FEED_DATA/arguments\"\n"); File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var processing = ResourceLock.Try(i.Paths, "processing");
        var scheduler = new Scheduler(i.Paths, new(i.Paths), i.Factory, NullLogger<Scheduler>.Instance, () => fake);
        await scheduler.Tick(default);
        await using (var db = i.Factory.Open()) { var r = await db.RunRequests.SingleAsync(); Assert.Equal("pending", r.Status); Assert.Equal(target, r.Person); Assert.Contains("cooldown", r.Note); await db.PlatformStates.ExecuteUpdateAsync(u => u.SetProperty(p => p.LastRunFinishedAt, Clock.Now.AddDays(-1))); }
        await scheduler.Tick(default);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true) { await using var db = i.Factory.Open(); if ((await db.RunRequests.SingleAsync(timeout.Token)).Status == "refused") break; await Task.Delay(20, timeout.Token); }
        var args = await File.ReadAllLinesAsync(i.Paths.Get("arguments"));
        Assert.Equal(target, args[Array.IndexOf(args, "--person") + 1]); Assert.Equal("10", args[Array.IndexOf(args, "--scrolls") + 1]); Assert.Equal("home", args[Array.IndexOf(args, "--mode") + 1]); Assert.DoesNotContain("--retry-incomplete", args);
    }
}
