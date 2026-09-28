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

public sealed class RecoveryHardeningTests
{
    static InstanceSnapshot Snapshot => new(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()), Scheduler = new() { Enabled = false } }, new(), Preferences.Parse(""));
    [Fact] public void LinkOnlyShareKeepsArticleSeparateFromCaption()
    {
        const string raw = """{"data":{"node":{"__typename":"Story","post_id":"101","creation_time":1700000000,"actors":[{"id":"501","name":"Fixture"}],"attachments":[{"styles":{"__typename":"StoryAttachmentShareSevereStyleRenderer","attachment":{"source":{"text":"example.test"},"target":{"__typename":"ExternalUrl"},"title_with_entities":{"text":"Synthetic article title"},"story_attachment_link_renderer":{"attachment":{"web_link":{"url":"https://example.test/article"}}}}}}]}}}""";
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics);
        var post = Assert.Single(parsed.Posts).Post; Assert.Null(post.Text); Assert.Equal("Synthetic article title", post.SharedText); Assert.Equal("https://example.test/article", post.SharedUrl);
    }
    [Fact] public void DecorativeUpstreamErrorIsWarningButTimelineErrorRemainsFailure()
    {
        const string raw = """{"data":{"node":{"__typename":"User","id":"501","profile_tile_sections":{}}},"errors":[{"message":"field_exception","path":["node","profile_tile_sections","edges",0,"image","uri"]}]}""";
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics); Assert.Contains("field_exception", Assert.Single(parsed.Warnings)); Assert.False(parsed.ExplicitEmpty); Assert.Empty(parsed.Posts);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("profile_tile_sections", "timeline_list_feed_units")).Diagnostics);
    }
    [Fact] public void UnavailableShareWithProfileMetadataIsNotAnEmptyTimeline()
    {
        const string raw = """{"data":{"user":{"id":"501","profile_pinned_post":null,"top_post_id":null,"timeline_list_feed_units":{"edges":[{"node":{"__typename":"Story","post_id":"101","creation_time":1700000000,"attachments":[{"styles":{"__typename":"StoryAttachmentUnavailableStyleRenderer"}}]}}]}}}}""";
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("StoryAttachmentUnavailableStyleRenderer", "NewUnknownRenderer")).Diagnostics);
    }
    [Fact] public void InstagramPlaceIconFailureDoesNotRejectAuthoredContent()
    {
        const string raw = """{"data":{"xdt_api__v1__feed__user_timeline_graphql_connection":{"edges":[{"node":{"pk":"101","code":"fixture","caption":{"text":"Synthetic caption"},"user":{"pk":"501","username":"fixture"},"location":{"profile_pic_url":null}}}]}},"errors":[{"message":"field_exception","path":["xdt_api__v1__feed__user_timeline_graphql_connection","edges",0,"node","location","profile_pic_url"]}]}""";
        var parsed = PayloadParser.Parse("instagram", raw); Assert.Empty(parsed.Diagnostics); Assert.Single(parsed.Warnings); Assert.Equal("Synthetic caption", Assert.Single(parsed.Posts).Post.Text);
        Assert.NotEmpty(PayloadParser.Parse("instagram", raw.Replace("location", "user")).Diagnostics);
    }
    [Fact] public void FacebookBusinessMetadataErrorDoesNotRejectPost()
    {
        const string raw = """{"data":{"user":{"timeline_list_feed_units":{"edges":[{"node":{"__typename":"Story","post_id":"101","creation_time":1700000000,"message":{"text":"Synthetic caption"}}}]},"delegate_page":{"ctwa_ad4ad_insights":null}}},"errors":[{"message":"field_exception","path":["user","delegate_page","ctwa_ad4ad_insights"]}]}""";
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics); Assert.Single(parsed.Warnings); Assert.Single(parsed.Posts);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("ctwa_ad4ad_insights", "unknown_field")).Diagnostics);
    }
    [Fact] public void TimelineCoverageAcceptsWallPostsAndCollaborationsButRejectsHomePrefetch()
    {
        var author = new Author { Platform = "facebook", PlatformAuthorId = "501", Url = "https://facebook.com/fixture" };
        const string wall = """{"data":{"node":{"__typename":"User","id":"501","timeline_list_feed_units":{"edges":[{"node":{"__typename":"Story","post_id":"101","creation_time":1700000000,"actors":[{"id":"502","name":"Other fixture"}],"message":{"text":"Happy birthday"}}}]}}}}""";
        var observation = Assert.Single(PayloadParser.Parse("facebook", wall).Posts);
        Assert.Equal("502", observation.AuthorKey); Assert.True(Feed.Cli.Site.TimelineAuthorMatches("facebook", author.Url, author, observation));
        Assert.False(Feed.Cli.Site.TimelineAuthorMatches("facebook", author.Url, author, observation with { TimelineOwnerIds = [] }));
        Assert.False(Feed.Cli.Site.TimelineAuthorMatches("facebook", author.Url, author, Assert.Single(PayloadParser.Parse("facebook", wall.Replace("timeline_list_feed_units", "news_feed")).Posts)));
        const string collaboration = """{"pk":"101","code":"fixture","user":{"pk":"502","username":"other"},"coauthor_producers":[{"pk":"501","username":"fixture"}]}""";
        author.Platform = "instagram"; author.Url = "https://instagram.com/fixture/";
        Assert.True(Feed.Cli.Site.TimelineAuthorMatches("instagram", author.Url, author, Assert.Single(PayloadParser.Parse("instagram", collaboration).Posts)));
        Assert.False(Feed.Cli.Site.TimelineAuthorMatches("instagram", author.Url, author, Assert.Single(PayloadParser.Parse("instagram", collaboration.Replace("coauthor_producers", "invited_coauthor_producers")).Posts)));
    }
    [Fact] public async Task ParserFailureWaitsForUpgradeAndExplicitReplayCanRecover()
    {
        await using var i = new TestInstance(); await i.Init();
        var file = i.Paths.Get("raw/facebook/fixture/001.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, "{\"unknown\":true}");
        using var http = new HttpClient(); var media = new MediaFiles(i.Paths, http); var ingest = new Ingest(i.Paths, i.Factory, media);
        Assert.Equal(1, (await ingest.File("facebook", file, Snapshot, true, default)).Failed);
        await using (var db = i.Factory.Open()) { Assert.Equal(PayloadParser.Version, (await db.RawSnapshots.SingleAsync()).BlockedParserVersion); await db.RawSnapshots.ExecuteUpdateAsync(u => u.SetProperty(r => r.AttemptedAt, Clock.Now.AddHours(-1))); }
        var worker = new Processing(i.Paths, i.Factory, new(http), media, ingest);
        Assert.Equal(0, (await worker.Run(Snapshot, new(), null, default)).Stage("facebook", "raw").Selected);
        await using (var db = i.Factory.Open()) await db.RawSnapshots.ExecuteUpdateAsync(u => u.SetProperty(r => r.BlockedParserVersion, PayloadParser.Version - 1));
        Assert.Equal(1, (await worker.Run(Snapshot, new(), null, default)).Stage("facebook", "raw").Selected);
        await File.WriteAllTextAsync(file, "{\"data\":{\"node\":{\"__typename\":\"GroupsYouShouldJoinFeedUnit\"}}}");
        Assert.Equal(0, (await ingest.File("facebook", file, Snapshot, true, default)).Failed);
        await using var check = i.Factory.Open(); var raw = await check.RawSnapshots.SingleAsync(); Assert.True(raw.Parsed); Assert.Null(raw.BlockedParserVersion); Assert.Null(raw.Error);
    }
    [Fact] public async Task ExactRepeatedImagesFoldWithoutMergingIdentitiesOrFeedback()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", DisplayName = "Fixture", RefsJson = "[\"fb:fixture\"]" }; db.Add(author); await db.SaveChangesAsync();
            for (int n = 0; n < 6; n++)
            {
                var p = new Post { Platform = "facebook", PlatformPostId = n.ToString(), AuthorId = author.Id, PostedAt = Clock.Now.AddSeconds(-n), Text = n == 3 ? "different caption" : null, Permalink = $"https://facebook.com/fixture/posts/{n}", MediaManifestJson = JsonSerializer.Serialize(new[] { new MediaSource("image", $"https://cdn.test/{n}.jpg", n.ToString()) }) };
                db.Add(p); await db.SaveChangesAsync(); db.Add(new Media { PostId = p.Id, SourceKey = n.ToString(), ContentHash = n == 4 ? "different-bytes" : n == 5 ? null : "same-bytes" }); db.Add(new Feedback { PostId = p.Id, Value = 1 });
            }
            await db.SaveChangesAsync();
        }
        // Authors explicitly protected from ordinary burst folding still fold exact repeats.
        var s = Snapshot with { Taxonomy = new() { Views = [new() { Key = "people", Label = "People", Authors = ["fb:fixture"] }] } };
        var page = await new FeedQuery(i.Factory).Read(s, "people", null, "live", default);
        Assert.Equal(6, page.VisibleCount); Assert.Equal(4, page.Items.Length); var repeat = Assert.Single(page.Items, u => u.Badge == "3 repeated posts"); Assert.Equal(2, repeat.Folded.Length);
        Assert.Equal(3, repeat.Folded.Append(repeat.Lead).Select(c => c.Post.Permalink).Distinct().Count()); Assert.All(repeat.Folded.Append(repeat.Lead), c => Assert.Equal(1, c.Up));
        Assert.Equal(6, (await new FeedQuery(i.Factory).Read(s, "people", null, "unsorted", default)).Items.Length);
        await using var check = i.Factory.Open(); Assert.Equal(6, await check.Posts.CountAsync()); Assert.Equal(6, await check.Feedback.CountAsync());
    }
    [Fact] public async Task TimelineRetriesAreDelayedBoundedAndNeverStartNewCycle()
    {
        await using var i = new TestInstance(); await i.Init(); await using (var db = i.Factory.Open()) { db.Add(new Author { Platform = "facebook", IsFriend = true }); await db.SaveChangesAsync(); }
        var coverage = new TimelineCoverage(i.Factory); Assert.Empty(await coverage.Select("facebook", 25, retriesOnly: true));
        var target = Assert.Single(await coverage.Select("facebook", 25));
        for (int attempt = 1; attempt <= TimelineCoverage.MaxAttempts; attempt++)
        {
            await coverage.Complete(new() { Platform = "facebook", AuthorId = target.AuthorId, Status = "rendered", PostsFound = 2 }, target);
            Assert.Empty(await coverage.Select("facebook", 25, retriesOnly: true));
            await using var db = i.Factory.Open(); var current = await db.SweepTargets.SingleAsync(); Assert.Equal(attempt, current.Attempts);
            if (attempt < TimelineCoverage.MaxAttempts) { Assert.Equal("retry", current.State); current.RetryAt = Clock.Now.AddSeconds(-1); await db.SaveChangesAsync(); target = Assert.Single(await coverage.Select("facebook", 25, retriesOnly: true)); }
            else Assert.Equal("done", current.State);
        }
        await using var check = i.Factory.Open(); var row = Assert.Single(await CoverageQuery.Read(check, Snapshot)); Assert.Equal("partial capture with errors", row.Status); Assert.Contains("Retry limit reached", row.Recovery); Assert.Equal(3, await check.TimelineVisits.CountAsync());
    }
    sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) => Task.FromResult(reply(r)); }
    [Fact] public async Task HealthyQueuedAndInflightWorkIsNotReportedAsFailure()
    {
        await using var i = new TestInstance(); await i.Init();
        await using var db = i.Factory.Open();
        db.Add(new RawSnapshot { Platform = "facebook", Path = "raw/facebook/fixture/new.json" });
        db.Add(new Post { Platform = "facebook", PlatformPostId = "fixture", IngestReadyAt = Clock.Now, LlmAttemptedAt = Clock.Now }); await db.SaveChangesAsync();
        Assert.Empty(await RecoveryQuery.Read(db, Snapshot));
        await db.Posts.ExecuteUpdateAsync(u => u.SetProperty(p => p.LlmAttemptedAt, Clock.Now.AddHours(-1)));
        Assert.Single(await RecoveryQuery.Read(db, Snapshot));
    }
    [Fact] public async Task ModelFailureSurvivesRestartAndSuccessfulRetryClearsError()
    {
        await using var i = new TestInstance(); await i.Init(); await using (var db = i.Factory.Open()) { db.Add(new Post { Platform = "facebook", PlatformPostId = "fixture", IngestReadyAt = Clock.Now }); await db.SaveChangesAsync(); }
        bool fail = true;
        using var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = fail ? "length" : "stop", message = new { content = fail ? "" : "{\"score\":8,\"reason\":\"synthetic\"}", reasoning_content = fail ? "private reasoning" : "" } } } })) });
        using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http); var s = Snapshot with { Config = Snapshot.Config with { Llm = new() { Enabled = true, BaseUrl = "http://fixture.test/v1", Vision = false } } };
        Processing Worker() => new(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media));
        Assert.Equal(1, (await Worker().Run(s, new("rescore"), null, default)).Failed);
        await using (var db = i.Factory.Open()) { var p = await db.Posts.SingleAsync(); Assert.False(p.Hidden); Assert.Equal(1, p.LlmFailures); Assert.Contains("token budget exhausted", p.LlmError); Assert.DoesNotContain("private reasoning", p.LlmError); var issue = Assert.Single(await RecoveryQuery.Read(db, s)); Assert.Contains("scheduler disabled", issue.Recovery); Assert.NotNull(issue.RetryAfter); }
        Assert.Equal(0, (await Worker().Run(s, new(), null, default)).Stage("facebook", "judge").Selected);
        fail = false; Assert.Equal(1, (await Worker().Run(s, new("rescore"), null, default)).Completed);
        await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync(); Assert.Null(post.LlmError); Assert.Equal(0, post.LlmFailures); Assert.True(post.Judged);
    }
}
