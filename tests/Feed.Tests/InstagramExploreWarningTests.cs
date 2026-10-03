using System.Text.Json.Nodes;
using Feed.Core.Infrastructure;
using Xunit;

namespace Feed.Tests;

public sealed class InstagramExploreWarningTests
{
    const string Alias = "_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_explore_story_media";
    static JsonObject Fixture()
    {
        static JsonObject Media(string id) => new() {
            ["pk"] = id, ["code"] = "synthetic" + id,
            ["user"] = new JsonObject { ["pk"] = "501", ["username"] = "fixture" },
            ["caption"] = new JsonObject { ["text"] = "Synthetic caption " + id },
            ["image_versions2"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["width"] = 640, ["url"] = "https://media.example.test/image.jpg" }) },
            ["location"] = new JsonObject { ["profile_pic_url"] = null }
        };
        return new JsonObject {
            ["data"] = new JsonObject { ["xdt_api__v1__feed__timeline__connection"] = new JsonObject { ["edges"] = new JsonArray(
                new JsonObject { ["node"] = new JsonObject { ["media"] = Media("101") } },
                new JsonObject { ["node"] = new JsonObject { ["explore_story"] = new JsonObject { ["media"] = Media("102") } } },
                new JsonObject { ["node"] = new JsonObject { ["media"] = Media("103") } }) } },
            ["errors"] = new JsonArray(new JsonObject { ["message"] = "execution error", ["path"] = new JsonArray(Alias, 0, "node", "location", "profile_pic_url") })
        };
    }
    static JsonArray Edges(JsonObject raw) => raw["data"]!["xdt_api__v1__feed__timeline__connection"]!["edges"]!.AsArray();
    static JsonObject ExploreMedia(JsonObject raw) => Edges(raw)[1]!["node"]!["explore_story"]!["media"]!.AsObject();

    [Fact] public void RecognizedPlaceIconAliasPreservesAllPostsAndMediaWithoutClaimingEmpty()
    {
        var raw = Fixture(); var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Empty(parsed.Diagnostics); Assert.Single(parsed.Warnings); Assert.False(parsed.ExplicitEmpty);
        Assert.Equal(new[] { "101", "102", "103" }, parsed.Posts.Select(p => p.Post.PlatformPostId));
        Assert.All(parsed.Posts, p => { Assert.Single(p.Media); Assert.Equal("501", p.AuthorKey); Assert.StartsWith("Synthetic caption", p.Post.Text); });
        raw.Remove("errors"); var clean = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Equal(clean.Posts.Select(p => p.Post.Text), parsed.Posts.Select(p => p.Post.Text));
        Assert.Equal(clean.Posts.SelectMany(p => p.Media), parsed.Posts.SelectMany(p => p.Media));
    }

    [Theory]
    [InlineData("caption")] [InlineData("user")] [InlineData("image_versions2")]
    [InlineData("unknown_alias")] [InlineData("negative_index")] [InlineData("other_index")]
    [InlineData("unknown_leaf")] [InlineData("missing_media")] [InlineData("missing_author")]
    [InlineData("missing_id")] [InlineData("missing_code")] [InlineData("missing_icon")]
    [InlineData("nonnull_icon")] [InlineData("ambiguous_branch")]
    public void ContentErrorsAndUnsupportedShapesRemainFailures(string change)
    {
        var raw = Fixture(); var path = raw["errors"]![0]!["path"]!.AsArray();
        switch(change) {
            case "caption": case "user": case "image_versions2": path[3] = change; break;
            case "unknown_alias": path[0] = Alias + "_unknown"; break;
            case "negative_index": path[1] = -1; break;
            case "other_index": path[1] = 1; break;
            case "unknown_leaf": path[4] = "unknown"; break;
            case "missing_media": Edges(raw)[1]!["node"]!["explore_story"]!["media"] = null; break;
            case "missing_author": ExploreMedia(raw).Remove("user"); break;
            case "missing_id": ExploreMedia(raw).Remove("pk"); break;
            case "missing_code": ExploreMedia(raw).Remove("code"); break;
            case "missing_icon": ExploreMedia(raw)["location"]!.AsObject().Remove("profile_pic_url"); break;
            case "nonnull_icon": ExploreMedia(raw)["location"]!["profile_pic_url"] = "https://media.example.test/icon.jpg"; break;
            case "ambiguous_branch": Edges(raw).Add(Edges(raw)[1]!.DeepClone()); break;
        }
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Warnings); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
    }

    [Fact] public void HarmlessIconWarningDoesNotMaskAnotherContentError()
    {
        var raw = Fixture(); var error = raw["errors"]![0]!.DeepClone(); error["path"]![3] = "caption";
        raw["errors"]!.AsArray().Add(error);
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Single(parsed.Warnings); Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
    }
}
