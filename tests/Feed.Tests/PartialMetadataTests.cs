using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Infrastructure;
using Xunit;

namespace Feed.Tests;

internal static class PartialMetadataFixture
{
    public const string Connection = "xdt_api__v1__feed__timeline__connection";
    public static JsonObject Media(string id = "101") => new() {
        ["pk"] = id, ["code"] = "synthetic" + id, ["caption"] = new JsonObject { ["text"] = "Synthetic caption" },
        ["user"] = new JsonObject { ["pk"] = "501", ["username"] = "fixture", ["full_name"] = "Synthetic Author" },
        ["display_url"] = "https://media.example.test/" + id + ".png"
    };
    public static object[] Prefix(bool explore) => explore ? [Connection, "edges", 0, "node", "explore_story", "media"] : [Connection, "edges", 0, "node", "media"];
    public static JsonObject Error(params object[] path) => new() { ["message"] = "synthetic execution error", ["path"] = new JsonArray(path.Select(p => JsonSerializer.SerializeToNode(p)).ToArray()) };
    public static JsonObject Raw(JsonObject media, bool explore = false, params object[][] errors) => new() {
        ["data"] = new JsonObject { [Connection] = new JsonObject { ["edges"] = new JsonArray(new JsonObject { ["node"] = explore
            ? new JsonObject { ["media"] = null, ["explore_story"] = new JsonObject { ["media"] = media, ["ad"] = null } }
            : new JsonObject { ["media"] = media } }) } },
        ["errors"] = new JsonArray(errors.Select(p => (JsonNode)Error(p)).ToArray())
    };
}
public sealed class PartialMetadataTests
{
    public static TheoryData<bool, string> MetadataCases
    {
        get
        {
            var rows = new TheoryData<bool, string>();
            foreach (var explore in new[] { false, true })
                foreach (var metadataField in new[] { "ai_interactive_embodiment_attachment_style_info", "ai_label_info", "audience", "brs_severity", "can_reshare", "carousel_parent_id", "follow_hashtag_info", "headline", "link", "link_text", "story_cta" })
                    rows.Add(explore, metadataField);
            return rows;
        }
    }
    [Theory, MemberData(nameof(MetadataCases))]
    public void KnownMetadataRetainsUsablePostWithExplicitPartialWarning(bool explore, string field)
    {
        var media = PartialMetadataFixture.Media(); media[field] = null;
        var parsed = PayloadParser.Parse("instagram", PartialMetadataFixture.Raw(media, explore, [..PartialMetadataFixture.Prefix(explore),field]).ToJsonString());
        Assert.Empty(parsed.Diagnostics); Assert.Contains("partial metadata unavailable", Assert.Single(parsed.Warnings));
        var post = Assert.Single(parsed.Posts); Assert.True(post.IsPartial); Assert.Equal("Synthetic caption", post.Post.Text); Assert.Single(post.Media); Assert.False(parsed.ExplicitEmpty);
    }
    [Theory]
    [InlineData("carousel_media")] [InlineData("clips_metadata")] [InlineData("code")] [InlineData("headline")]
    [InlineData("link")] [InlineData("organic_tracking_token")] [InlineData("story_cta")] [InlineData("user")]
    public void CarouselChildMetadataUsesIntactParentAndAllIdentifiedSlideAssets(string field)
    {
        var media = PartialMetadataFixture.Media(); var slide = new JsonObject { ["pk"] = "201", ["display_url"] = "https://media.example.test/slide.png", [field] = null };
        media["carousel_media"] = new JsonArray(slide); media["carousel_media_count"] = 1;
        var parsed = PayloadParser.Parse("instagram", PartialMetadataFixture.Raw(media, false, [..PartialMetadataFixture.Prefix(false),"carousel_media",0,field]).ToJsonString());
        Assert.Empty(parsed.Diagnostics); Assert.True(Assert.Single(parsed.Posts).IsPartial); Assert.Single(parsed.Warnings);
        Assert.Equal("https://media.example.test/slide.png", Assert.Single(parsed.Posts[0].Media).Url);
    }
    [Theory]
    [InlineData("aigm_account_label_info")] [InlineData("is_unpublished")] [InlineData("supervision_info")]
    public void CoauthorMetadataNeedsSurvivingIdentity(string field)
    {
        var media = PartialMetadataFixture.Media(); var coauthor = new JsonObject { ["pk"] = "502", [field] = null };
        media["coauthor_producers"] = new JsonArray(coauthor);
        var raw = PartialMetadataFixture.Raw(media, false, [..PartialMetadataFixture.Prefix(false),"coauthor_producers",0,field]);
        Assert.True(Assert.Single(PayloadParser.Parse("instagram", raw.ToJsonString()).Posts).IsPartial);
        coauthor.Remove("pk"); Assert.NotEmpty(PayloadParser.Parse("instagram", raw.ToJsonString()).Diagnostics);
    }
    [Fact] public void TaggedUserMetadataNeedsSurvivingIdentity()
    {
        var media = PartialMetadataFixture.Media(); var user = new JsonObject { ["pk"] = "502", ["aigm_account_label_info"] = null };
        media["usertags"] = new JsonObject { ["in"] = new JsonArray(new JsonObject { ["user"] = user }) };
        var raw = PartialMetadataFixture.Raw(media, false, [..PartialMetadataFixture.Prefix(false),"usertags","in",0,"user","aigm_account_label_info"]);
        Assert.True(Assert.Single(PayloadParser.Parse("instagram", raw.ToJsonString()).Posts).IsPartial);
        user.Remove("pk"); Assert.NotEmpty(PayloadParser.Parse("instagram", raw.ToJsonString()).Diagnostics);
    }
    [Theory]
    [InlineData("caption")] [InlineData("user")] [InlineData("pk")] [InlineData("code")] [InlineData("display_url")]
    [InlineData("unknown")] [InlineData("missing_leaf")] [InlineData("nonnull_leaf")]
    [InlineData("carousel_count")] [InlineData("slide_id")] [InlineData("slide_asset")]
    [InlineData("negative_index")] [InlineData("invalid_index")] [InlineData("pagination")]
    public void IncompleteCoreDataUnknownErrorsAndUnresolvedPathsStillFail(string change)
    {
        var media = PartialMetadataFixture.Media(); media["link"] = null; object[] path = [..PartialMetadataFixture.Prefix(false),"link"];
        switch(change) {
            case "caption": case "user": case "pk": case "code": case "display_url": media.Remove(change); break;
            case "unknown": media["unknown"] = null; path[^1] = "unknown"; break;
            case "missing_leaf": media.Remove("link"); break;
            case "nonnull_leaf": media["link"] = "https://example.test/"; break;
            case "carousel_count": case "slide_id": case "slide_asset":
                var slide = new JsonObject { ["pk"] = "201", ["display_url"] = "https://media.example.test/slide.png" };
                media["carousel_media"] = new JsonArray(slide); media["carousel_media_count"] = change == "carousel_count" ? 2 : 1;
                if(change == "slide_id") slide.Remove("pk"); if(change == "slide_asset") slide.Remove("display_url"); break;
            case "negative_index": path[2] = -1; break;
            case "invalid_index": path[2] = 3; break;
            case "pagination": path = [PartialMetadataFixture.Connection,"page_info"]; break;
        }
        var parsed = PayloadParser.Parse("instagram", PartialMetadataFixture.Raw(media, false, path).ToJsonString());
        Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Warnings); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
    }
    [Fact] public void ExploreAlternateBranchErrorsPreserveOnlyValidatedSurvivingMedia()
    {
        var media = PartialMetadataFixture.Media(); var raw = PartialMetadataFixture.Raw(media, true,
            [PartialMetadataFixture.Connection,"edges",0,"node","media"], [PartialMetadataFixture.Connection,"edges",0,"node","explore_story","ad"]);
        var parsed = PayloadParser.Parse("instagram", raw.ToJsonString());
        Assert.Empty(parsed.Diagnostics); Assert.Equal(2,parsed.Warnings.Count); Assert.False(Assert.Single(parsed.Posts).IsPartial);
        media.Remove("display_url"); Assert.NotEmpty(PayloadParser.Parse("instagram",raw.ToJsonString()).Diagnostics);
    }
    [Fact] public void ContentErrorIsNotHiddenByPartialMetadataWarning()
    {
        var media = PartialMetadataFixture.Media(); media["link"] = null;
        var raw = PartialMetadataFixture.Raw(media, false, [..PartialMetadataFixture.Prefix(false),"link"], [..PartialMetadataFixture.Prefix(false),"caption"]);
        var parsed = PayloadParser.Parse("instagram",raw.ToJsonString()); Assert.Single(parsed.Warnings); Assert.NotEmpty(parsed.Diagnostics); Assert.Empty(parsed.Posts);
    }
    [Theory, InlineData(false), InlineData(true)] public void CompleteObservationWinsOverPartialDuplicate(bool completeFirst)
    {
        var media = PartialMetadataFixture.Media(); media["link"] = null;
        var partial = PartialMetadataFixture.Raw(media, false, [..PartialMetadataFixture.Prefix(false),"link"]).ToJsonString();
        var complete = PartialMetadataFixture.Raw(PartialMetadataFixture.Media()).ToJsonString();
        var parsed = PayloadParser.Parse("instagram",completeFirst ? complete+"\n"+partial : partial+"\n"+complete);
        Assert.False(Assert.Single(parsed.Posts).IsPartial); Assert.Single(parsed.Warnings); Assert.Empty(parsed.Diagnostics);
    }
    static JsonObject Composer() => JsonNode.Parse("""{"data":{"viewer":{"actor":{"id":"501"},"feed_comet_composer":{"sprouts":[]},"eligible_promotions":{"nodes":[]}},"privacy_selector":{"privacy_scope_renderer":{"__typename":"SyntheticPrivacyRenderer","privacy_row_input":{},"scope":{}}}}}""")!.AsObject();
    [Fact] public void ExpandedComposerIsAuxiliaryWithoutClaimingAnEmptyFeed()
    {
        var parsed = PayloadParser.Parse("facebook",Composer().ToJsonString()); Assert.Empty(parsed.Posts); Assert.Empty(parsed.Diagnostics); Assert.False(parsed.ExplicitEmpty);
    }
    [Theory]
    [InlineData("root_field")] [InlineData("viewer_field")] [InlineData("selector_field")] [InlineData("missing_renderer")] [InlineData("upstream_error")]
    public void UnknownComposerFieldsAndErrorsStayDiagnostic(string change)
    {
        var raw = Composer();
        switch(change) {
            case "root_field": raw["data"]!["unknown"] = new JsonObject(); break;
            case "viewer_field": raw["data"]!["viewer"]!["news_feed"] = new JsonObject(); break;
            case "selector_field": raw["data"]!["privacy_selector"]!["unknown"] = new JsonObject(); break;
            case "missing_renderer": raw["data"]!["privacy_selector"]!.AsObject().Remove("privacy_scope_renderer"); break;
            case "upstream_error": raw["errors"] = new JsonArray(PartialMetadataFixture.Error("viewer","actor")); break;
        }
        Assert.NotEmpty(PayloadParser.Parse("facebook",raw.ToJsonString()).Diagnostics);
    }
}
