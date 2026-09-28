using Feed.Cli;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Web;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Feed.Tests;

public sealed class ScheduleConfigurationTests
{
    const string IntervalConfig = """{"platforms":{"facebook":{"schedule_intervals":{"ALL-FOLLOWED":{"days":2,"start_date":"2028-02-28"}}}}} """;

    [Theory]
    [InlineData("2028-02-27", false)]
    [InlineData("2028-02-28", true)]
    [InlineData("2028-02-29", false)]
    [InlineData("2028-03-01", true)]
    [InlineData("2028-03-02", false)]
    [InlineData("2028-03-03", true)]
    public void CalendarIntervalSurvivesReloadAndMonthBoundary(string date, bool due)
    {
        for (var restart = 0; restart < 2; restart++)
        {
            var c = InstanceValidation.Parse<FeedConfig>(IntervalConfig);
            InstanceValidation.Validate(c, new());
            Assert.Equal(due, c.Platform("facebook").ScheduledOn("all_followed", DateOnly.Parse(date)));
            Assert.True(c.Platform("facebook").ScheduledOn("home", DateOnly.Parse(date)));
        }
    }

    [Fact]
    public void IntervalUsesLocalCalendarAcrossDaylightSavingAndIntersectsWeekdays()
    {
        var c = InstanceValidation.Parse<FeedConfig>("""{"timezone":"Europe/Prague","platforms":{"facebook":{"schedule_intervals":{"home":{"days":2,"start_date":"2028-03-25"}},"schedule_days":{"home":["mon"]}}}}""");
        InstanceValidation.Validate(c, new());
        Assert.False(c.Platform("facebook").ScheduledOn("home", new(2028, 3, 25))); // Saturday, despite interval.
        var local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(2028, 3, 26, 22, 30, 0, DateTimeKind.Utc), c.Zone);
        Assert.Equal(new DateOnly(2028, 3, 27), DateOnly.FromDateTime(local));
        Assert.True(c.Platform("facebook").ScheduledOn("home", DateOnly.FromDateTime(local)));
        Assert.False(c.Platform("facebook").ScheduledOn("home", new(2028, 4, 3))); // Monday, outside interval.
    }

    [Theory]
    [InlineData("{\"days\":0,\"start_date\":\"2028-01-01\"}")]
    [InlineData("{\"days\":-1,\"start_date\":\"2028-01-01\"}")]
    [InlineData("{\"days\":2}")]
    [InlineData("{\"days\":2,\"start_date\":\"invalid\"}")]
    public void InvalidIntervalsFailValidation(string interval) => Assert.ThrowsAny<Exception>(() =>
        InstanceValidation.Validate(InstanceValidation.Parse<FeedConfig>("{\"platforms\":{\"facebook\":{\"schedule_intervals\":{\"home\":" + interval + "}}}}"), new()));

    [Fact]
    public async Task SchedulerSkipsOffDayAndFiresDueDayOnlyOnceAcrossRestart()
    {
        await using var i = new TestInstance(); await i.Init();
        using var processing = ResourceLock.Try(i.Paths, "processing");
        var today = DateOnly.FromDateTime(Clock.Now);
        var slot = Clock.Now.ToString("HH:mm");
        var path = i.Paths.Get("config.json");
        string Config(DateOnly start) => """{"timezone":"UTC","platforms":{"facebook":{"schedule":{"home":["SLOT"]},"schedule_intervals":{"home":{"days":2,"start_date":"START"}}}},"scheduler":{"jitter_minutes":0},"backgrounds":{"source":"off"}}""".Replace("SLOT", slot).Replace("START", start.ToString("yyyy-MM-dd"));
        // Avoid unrelated retention work in this scheduling test.
        await using (var db = i.Factory.Open()) await db.Put("maintenance:last", Clock.Now.ToString("yyyy-MM-dd"));
        await File.WriteAllTextAsync(path, Config(today.AddDays(-1)));
        Scheduler NewScheduler() => new(i.Paths, new(i.Paths), i.Factory, NullLogger<Scheduler>.Instance, () => i.Paths.Get("missing-cli"));
        await NewScheduler().Tick(default);
        await using (var db = i.Factory.Open()) Assert.Empty(await db.RunRequests.ToListAsync());
        await File.WriteAllTextAsync(path, Config(today));
        await Assert.ThrowsAsync<FileNotFoundException>(() => NewScheduler().Tick(default));
        await NewScheduler().Tick(default);
        await using (var db = i.Factory.Open())
        {
            Assert.Single(await db.RunRequests.ToListAsync());
            Assert.Equal(today.ToString("yyyy-MM-dd"), await db.Get($"slot:facebook:home:{slot}"));
        }
    }

    [Fact]
    public void HomeCompanionsCombineWithCloseFriendsWithoutDuplicateVisitsOrCrossPlatformMatches()
    {
        Author[] authors = [
            new() { Id = 1, Platform = "facebook", PlatformAuthorId = "101", RefsJson = "[\"fb:101\"]", IsFriend = true, Url = "https://www.facebook.com/101" },
            new() { Id = 2, Platform = "facebook", PlatformAuthorId = "102", RefsJson = "[\"fb:102\"]", IsFriend = true, Url = "https://www.facebook.com/102" },
            new() { Id = 3, Platform = "instagram", PlatformAuthorId = "103", RefsJson = "[\"ig:103\"]", IsFriend = true, Url = "https://www.instagram.com/synthetic/" },
            new() { Id = 4, Platform = "facebook", PlatformAuthorId = "104", RefsJson = "[\"fb:104\"]", IsFriend = false, Url = "https://www.facebook.com/104" }
        ];
        var config = new PlatformConfig { HomeTimelineAuthors = ["fb:101", "ig:103", "fb:104"], CloseFriends = ["fb:101", "fb:102"] };
        Assert.Equal([1L], Capture.HomeTimelines("facebook", config, new(), authors).Select(a => a.Id));
        Assert.Equal([1L, 2L], Capture.HomeTimelines("facebook", config, new("close_friends"), authors).Select(a => a.Id));
        Assert.Equal([1L], Capture.HomeTimelines("facebook", config, new("close_friends", Friends: []), authors).Select(a => a.Id));
        Assert.Empty(Capture.HomeTimelines("facebook", config, new(Person: "101"), authors));
        Assert.Empty(Capture.HomeTimelines("facebook", config, new("all_followed"), authors));
        Assert.Empty(Capture.HomeTimelines("facebook", new(), new(), authors));
    }
}
