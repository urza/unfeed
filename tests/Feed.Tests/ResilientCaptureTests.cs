using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;

public sealed class ResilientCaptureTests
{
    static string Story(long timestamp, string? title = "Fixture moved to a new town", bool life = false) => JsonSerializer.Serialize(new {
        __typename = "Story", post_id = "123", creation_time = timestamp, actors = new[] { new { id = "456", name = "Fixture" } },
        comet_sections = new { context_layout = new { story = new { comet_sections = new { title = new { story = new { title = title is null ? null : new { text = title } } } } } } },
        attachments = life ? new object[] { new { styles = new { __typename = "StoryAttachmentLifeEventStyleRenderer", attachment = new { properties = new[] { new { value = new { text = "Started a new job" } }, new { value = new { text = "https://static.example.test/icon.png" } } } } } } } : []
    });
    [Theory][InlineData(-315619200)][InlineData(1700000000)]
    public void LifeEventsKeepStructuredTextAndAuthorWithoutInventingCaption(long time)
    {
        var p = Assert.Single(PayloadParser.Parse("facebook", Story(time, null, true)).Posts);
        Assert.Equal("Started a new job", p.Post.StoryTitle); Assert.Null(p.Post.Text); Assert.Empty(p.Media);
        Assert.Equal("456", p.AuthorKey); Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(time).UtcDateTime, p.Post.PostedAt);
    }
    [Fact] public void TitleOnlyStoriesRequireIdentityTimeAndContent()
    {
        Assert.Single(PayloadParser.Parse("facebook", Story(1700000000)).Posts);
        Assert.Empty(PayloadParser.Parse("facebook", Story(0)).Posts);
        Assert.Empty(PayloadParser.Parse("facebook", Story(long.MinValue)).Posts);
        Assert.Empty(PayloadParser.Parse("facebook", Story(1700000000, null)).Posts);
        Assert.Empty(PayloadParser.Parse("facebook", Story(1700000000).Replace("\"id\":\"456\"", "\"unknown\":\"456\"")).Posts);
    }
    [Theory][InlineData("preferred_body")][InlineData("body_renderer")]
    public void CommentVideoMetadataRequiresExactBoundaryAndRejectsContent(string field)
    {
        var raw = JsonSerializer.Serialize(new { label = "VideoPlayerRelay_video$defer$InstreamVideoAdBreaksPlayer_video", path = new object[] { "node", "timeline_list_feed_units", "edges", 2, "node", "comet_sections", "feedback", "story", "story_ufi_container", "story", "feedback_context", "interesting_top_level_comments", 0, "comment", field, "ranges", 1, "entity" }, data = new { id = "synthetic-video", instream_extra_config = (object?)null, instream_video_ad_breaks_comet = new { } } });
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("interesting_top_level_comments", "unknown_content")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("\"instream_extra_config\":null", "\"instream_extra_config\":{\"post_id\":\"hidden\"}")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("VideoPlayerRelay_video", "Unknown")).Diagnostics);
    }
    const string Unknown = """{"data":{"future_item":{"text":"unrecognized"}}}""";
    const string Auxiliary = """{"label":"ProfileCometProfilePicture_user$defer$ProfileCometNewPictureMenu_user","path":["user","profile_header_renderer","user"],"data":{"story_bucket":{"nodes":[]},"cosmetic_hint":true}}""";
    [Fact] public void RecoverySelectsIntactUnknownRootsWithSourceIndicesAndBoundedRequest()
    {
        var body = Story(1700000000) + new string(' ', 50000) + "\n" + Auxiliary;
        var input = Assert.IsType<RecoveryInput>(RecoveryInput.Build("facebook", body, null, PayloadParser.Parse("facebook", body), 12000));
        Assert.Equal(new[] { 1 }, input.SourceRoots); Assert.Equal(Auxiliary, input.Body);
        Assert.True(JsonSerializer.Serialize(input.Messages).Length <= 12000);
        var proposal = input.Merge(body, new([new(0, "auxiliary", "Profile controls", ["/data/story_bucket"])]));
        var parsed = CaptureRecovery.Validate("facebook", body, proposal);
        Assert.Empty(parsed.Diagnostics); Assert.Single(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
        Assert.Throws<FormatException>(() => input.Merge(body, new([new(1, "auxiliary", "Wrong index", [""])])));
    }
    [Fact] public void PartialOverlayPreservesOversizedSiblingAndCanReplay()
    {
        var body = Unknown + new string(' ', 50000) + "\n" + Auxiliary;
        var input = Assert.IsType<RecoveryInput>(RecoveryInput.Build("facebook", body, null, PayloadParser.Parse("facebook", body), 12000));
        var proposal = input.Merge(body, new([new(0, "auxiliary", "Profile controls", ["/data/story_bucket"])]));
        var parsed = CaptureRecovery.Validate("facebook", body, proposal);
        Assert.Single(parsed.Diagnostics); Assert.Contains(parsed.Parts, p => p.Root == 0 && p.Kind == "unknown");
        Assert.Contains(parsed.Parts, p => p.Root == 1 && p.Kind == "auxiliary_model_validated");
        Assert.Null(RecoveryInput.Build("facebook", body, null, parsed, 12000));
    }
    [Fact] public void UnknownBranchesInsideOneEnvelopeAreNotCutAwayToFit()
    {
        var body = "{\"data\":{\"padding\":\"" + new string('x', 50000) + "\",\"unknown\":{}}}";
        Assert.Null(RecoveryInput.Build("facebook", body, null, PayloadParser.Parse("facebook", body), 12000));
    }
    [Fact] public async Task ParserBlockedVisitRetainsGapWithoutBrowserRetry()
    {
        await using var i = new TestInstance(); await i.Init();
        await using(var db = i.Factory.Open()) { db.Authors.Add(new() { Platform = "facebook", IsFriend = true }); await db.SaveChangesAsync(); }
        var coverage = new TimelineCoverage(i.Factory); var target = Assert.Single(await coverage.Select("facebook", 1));
        await coverage.Complete(new() { Platform = "facebook", AuthorId = target.AuthorId, Status = "rendered", CaptureStatus = "incomplete", StopReason = "parser_blocked", PostsFound = 2 }, target);
        await using var check = i.Factory.Open(); var saved = await check.SweepTargets.SingleAsync();
        Assert.Equal("done", saved.State); Assert.Null(saved.RetryAt); Assert.Equal("incomplete", (await check.TimelineVisits.SingleAsync()).CaptureStatus);
    }
    [Fact] public void CollectionSummaryDistinguishesFailedRetriesAndTargetedChecks()
    {
        var run = new Run { Platform = "facebook", Kind = "collect", Mode = "all_followed", Trigger = "recovery", Status = "error", FinishedAt = Clock.Now, StatsJson = "{\"visited\":5}" };
        Assert.Contains("gap retry", CollectionSummary.Describe(run)); Assert.Contains("errors", CollectionSummary.Describe(run));
        run.StatsJson = "{\"scope\":\"person\",\"visited\":1,\"incomplete\":1}"; run.Status = "ok";
        Assert.Contains("profile check", CollectionSummary.Describe(run)); Assert.Contains("incomplete", CollectionSummary.Describe(run));
    }
}
