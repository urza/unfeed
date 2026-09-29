using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;

public sealed class PartialCaptureTests
{
    static JsonObject Story(string id) => new() { ["__typename"] = "Story", ["post_id"] = id, ["creation_time"] = 1700000000, ["actors"] = new JsonArray(new JsonObject { ["id"] = "501", ["name"] = "Synthetic" }), ["message"] = new JsonObject { ["text"] = "Synthetic caption" } };
    static JsonArray PathArray(params object[] path) => new(path.Select(p => JsonSerializer.SerializeToNode(p)).ToArray());
    static JsonObject Error(params object[] path) => new() { ["message"] = "synthetic failure", ["path"] = PathArray(path) };
    [Fact] public void CommentAttachmentIsWarningButPostAttachmentStillFails()
    {
        var story = Story("101");
        var raw = new JsonObject { ["data"] = new JsonObject { ["node"] = new JsonObject { ["timeline_list_feed_units"] = new JsonObject { ["edges"] = new JsonArray(new JsonObject { ["node"] = story }) } } }, ["errors"] = new JsonArray(Error("node", "timeline_list_feed_units", "edges", 0, "node", "comet_sections", "feedback", "story", "story_ufi_container", "story", "feedback_context", "interesting_top_level_comments", 0, "comment", "attached_story")) };
        var parsed = PayloadParser.Parse("facebook", raw.ToJsonString()); Assert.Single(parsed.Posts); Assert.Single(parsed.Warnings); Assert.Empty(parsed.Diagnostics);
        raw["errors"] = new JsonArray(Error("node", "timeline_list_feed_units", "edges", 0, "node", "attached_story"));
        parsed = PayloadParser.Parse("facebook", raw.ToJsonString()); Assert.Empty(parsed.Posts); Assert.NotEmpty(parsed.Diagnostics);
    }
    [Fact] public void EntityMetadataNeedsIntactCaptionAndValidRange()
    {
        var story = Story("101");
        var message = new JsonObject { ["text"] = "Synthetic text", ["ranges"] = new JsonArray(new JsonObject { ["offset"] = 0, ["length"] = 9, ["entity"] = null }) };
        story["comet_sections"] = new JsonObject { ["content"] = new JsonObject { ["story"] = new JsonObject { ["comet_sections"] = new JsonObject { ["message"] = new JsonObject { ["story"] = new JsonObject { ["message"] = message } } } } } };
        var raw = new JsonObject { ["data"] = new JsonObject { ["node"] = story }, ["errors"] = new JsonArray(Error("node", "comet_sections", "content", "story", "comet_sections", "message", "story", "message", "ranges", 0, "entity")) };
        var parsed = PayloadParser.Parse("facebook", raw.ToJsonString()); Assert.Empty(parsed.Diagnostics); Assert.Single(parsed.Warnings); Assert.Single(parsed.Posts);
        message["text"] = null;
        parsed = PayloadParser.Parse("facebook", raw.ToJsonString()); Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Posts);
    }
    [Fact] public async Task PartialResponseStoresOnlyUnaffectedEdgesAndKeepsFailureForReview()
    {
        await using var i = new TestInstance(); await i.Init();
        const string raw = """{"data":{"xdt_api__v1__feed__timeline__connection":{"edges":[{"node":{"media":{"pk":"101","code":"one","user":{"pk":"501","username":"synthetic"},"caption":{"text":"Intact"}}}},{"node":{"media":{"pk":"102","code":"two","user":{"pk":"501","username":"synthetic"},"caption":null}}}]}},"errors":[{"message":"synthetic","path":["xdt_api__v1__feed__timeline__connection","edges",1,"node","media","caption"]}]}""";
        var parsed = PayloadParser.Parse("instagram", raw); Assert.Equal("101", Assert.Single(parsed.Posts).Post.PlatformPostId); Assert.NotEmpty(parsed.Diagnostics); Assert.False(parsed.ExplicitEmpty);
        var file = i.Paths.Get("raw/instagram/synthetic/001.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!); await File.WriteAllTextAsync(file, raw);
        using var http = new HttpClient(); var ingest = new Ingest(i.Paths, i.Factory, new(i.Paths, http)); var s = new InstanceFiles(i.Paths).Current;
        var first = await ingest.File("instagram", file, s, true, default); Assert.Equal(1, first.New); Assert.Equal(1, first.Failed);
        var second = await ingest.File("instagram", file, s, true, default); Assert.Equal(0, second.New); Assert.Equal(0, second.Revised); Assert.Equal(1, second.Failed);
        await using var db = i.Factory.Open(); Assert.Equal("101", (await db.Posts.SingleAsync()).PlatformPostId); var capture = await db.RawSnapshots.SingleAsync(); Assert.False(capture.Parsed); Assert.Equal(PayloadParser.Version, capture.BlockedParserVersion);
    }
    [Fact] public void InstagramUnionErrorsPreserveMediaButNotMissingMediaOrUnknownFields()
    {
        const string raw = """{"data":{"xdt_api__v1__feed__timeline__connection":{"edges":[{"node":{"media":{"pk":"101","code":"one","user":{"pk":"501","username":"synthetic"}},"ad":null}},{"node":{"media":null}}]}},"errors":[{"message":"synthetic","path":["xdt_api__v1__feed__timeline__connection","edges",0,"node","ad"]},{"message":"synthetic","path":["xdt_api__v1__feed__timeline__connection","edges",1,"node","media"]}]}""";
        var parsed = PayloadParser.Parse("instagram", raw); Assert.Single(parsed.Posts); Assert.Single(parsed.Warnings); Assert.Single(parsed.Diagnostics);
        parsed = PayloadParser.Parse("instagram", raw.Replace("\"ad\"", "\"unknown\"")); Assert.Empty(parsed.Posts); Assert.Empty(parsed.Warnings); Assert.NotEmpty(parsed.Diagnostics);
    }
    [Fact] public void IndependentJsonLinesSurviveButUnknownGlobalErrorRejectsItsRoot()
    {
        var good = new JsonObject { ["data"] = new JsonObject { ["node"] = Story("101") } };
        var bad = new JsonObject { ["data"] = new JsonObject { ["node"] = Story("102") }, ["errors"] = new JsonArray(Error("unknown")) };
        var parsed = PayloadParser.Parse("facebook", good.ToJsonString() + "\n" + bad.ToJsonString()); Assert.Equal("101", Assert.Single(parsed.Posts).Post.PlatformPostId); Assert.NotEmpty(parsed.Diagnostics);
    }
}
