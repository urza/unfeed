using System.Text.Json.Nodes;
using Feed.Core.Infrastructure;
using Xunit;

namespace Feed.Tests;

// Observed 2026-10-16 and 2026-10-29: Instagram reports a null place icon on a feed item's own media
// under a fragment alias whose index counts non-null media branches, not timeline edges.
public sealed class InstagramFeedItemIconWarningTests
{
    const string Alias = "_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_media";
    static JsonObject Media(string id, bool icon = true) => new() {
        ["pk"] = id, ["code"] = "synthetic" + id,
        ["user"] = new JsonObject { ["pk"] = "501", ["username"] = "fixture" },
        ["caption"] = new JsonObject { ["text"] = "Synthetic caption " + id },
        ["image_versions2"] = new JsonObject { ["candidates"] = new JsonArray(new JsonObject { ["width"] = 640, ["url"] = "https://media.example.test/image.jpg" }) },
        ["location"] = icon ? new JsonObject { ["pk"] = "77", ["name"] = "Place", ["profile_pic_url"] = null } : null
    };
    // Edge 1 has no media (an Explore unit) and edge 2 has media without a location, so alias
    // index 1 must land on edge 3: null media slots do not count, media without a place does.
    static JsonObject Fixture() => new() {
        ["data"] = new JsonObject { ["xdt_api__v1__feed__timeline__connection"] = new JsonObject { ["edges"] = new JsonArray(
            new JsonObject { ["node"] = new JsonObject { ["media"] = Media("101") } },
            new JsonObject { ["node"] = new JsonObject { ["media"] = null, ["explore_story"] = new JsonObject { ["media"] = Media("102") } } },
            new JsonObject { ["node"] = new JsonObject { ["media"] = Media("103", icon: false) } },
            new JsonObject { ["node"] = new JsonObject { ["media"] = Media("104") } }) } },
        ["errors"] = new JsonArray(
            new JsonObject { ["message"] = "execution error", ["severity"] = "ERROR", ["path"] = new JsonArray(Alias, 0, "node", "location", "profile_pic_url") },
            new JsonObject { ["message"] = "execution error", ["severity"] = "ERROR", ["path"] = new JsonArray(Alias, 2, "node", "location", "profile_pic_url") })
    };
    static JsonArray Edges(JsonObject raw) => raw["data"]!["xdt_api__v1__feed__timeline__connection"]!["edges"]!.AsArray();
    static JsonObject MediaAt(JsonObject raw, int edge) => Edges(raw)[edge]!["node"]!["media"]!.AsObject();

    [Fact] public void FeedItemIconAliasPreservesAllPostsAsWarnings()
    {
        var raw = Fixture(); var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Empty(parsed.Diagnostics); Assert.Equal(2, parsed.Warnings.Count); Assert.False(parsed.ExplicitEmpty);
        Assert.Equal(new[] { "101", "102", "103", "104" }, parsed.Posts.Select(p => p.Post.PlatformPostId));
        Assert.All(parsed.Posts, p => { Assert.False(p.IsPartial); Assert.Single(p.Media); Assert.StartsWith("Synthetic caption", p.Post.Text); });
        raw.Remove("errors"); var clean = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Equal(clean.Posts.Select(p => p.Post.Text), parsed.Posts.Select(p => p.Post.Text));
    }

    [Fact] public void AliasIndexSkipsNullMediaSlotsAndCountsMediaWithoutPlace()
    {
        // Index 1 is edge 2, whose media has no location at all: the icon is not explicitly null, so this is a failure.
        var raw = Fixture(); raw["errors"]!.AsArray().RemoveAt(1); raw["errors"]![0]!["path"]![1] = 1;
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Warnings); Assert.Empty(parsed.Posts);
    }

    [Theory]
    [InlineData("out_of_range")] [InlineData("negative")] [InlineData("caption")] [InlineData("user")]
    [InlineData("unknown_alias")] [InlineData("unknown_leaf")] [InlineData("missing_icon")] [InlineData("nonnull_icon")]
    [InlineData("missing_author")] [InlineData("missing_id")] [InlineData("missing_code")] [InlineData("missing_image")] [InlineData("ambiguous_branch")]
    public void ContentErrorsAndUnverifiedShapesRemainFailures(string change)
    {
        var raw = Fixture(); raw["errors"]!.AsArray().RemoveAt(1); var path = raw["errors"]![0]!["path"]!.AsArray();
        switch (change) {
            case "out_of_range": path[1] = 3; break;
            case "negative": path[1] = -1; break;
            case "caption": case "user": path[3] = change; break;
            case "unknown_alias": path[0] = Alias + "_unknown"; break;
            case "unknown_leaf": path[4] = "unknown"; break;
            case "missing_icon": MediaAt(raw, 0)["location"]!.AsObject().Remove("profile_pic_url"); break;
            case "nonnull_icon": MediaAt(raw, 0)["location"]!["profile_pic_url"] = "https://media.example.test/icon.jpg"; break;
            case "missing_author": MediaAt(raw, 0).Remove("user"); break;
            case "missing_id": MediaAt(raw, 0).Remove("pk"); break;
            case "missing_code": MediaAt(raw, 0).Remove("code"); break;
            case "missing_image": MediaAt(raw, 0).Remove("image_versions2"); break;
            case "ambiguous_branch": Edges(raw).Add(Edges(raw)[0]!.DeepClone()); break;
        }
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Warnings); Assert.False(parsed.ExplicitEmpty);
        Assert.DoesNotContain(parsed.Posts, p => p.Post.PlatformPostId == "101");
    }

    [Fact] public void HarmlessIconWarningDoesNotMaskAnotherContentError()
    {
        var raw = Fixture(); var error = raw["errors"]![0]!.DeepClone(); error["path"]![3] = "caption";
        raw["errors"]!.AsArray().Add(error);
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Equal(2, parsed.Warnings.Count); Assert.NotEmpty(parsed.Diagnostics); Assert.False(parsed.ExplicitEmpty);
        Assert.DoesNotContain(parsed.Posts, p => p.Post.PlatformPostId == "101");
    }
}
