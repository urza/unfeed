using Feed.Cli;
using Feed.Core.Application;
using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Xunit;
namespace Feed.Tests;
public sealed class ParserTests
{
    [Theory]
    [InlineData("Story", "GenericAttachmentMedia")]
    [InlineData("GoodwillThrowbackCard", "GenericAttachmentMedia")]
    [InlineData("Photo", "Photo")]
    [InlineData(null, "Video")]
    public void FacebookNativeMemoryAndAnimatedMediaLinksAreNotExternalShares(string? target, string mediaType)
    {
        var raw = JsonSerializer.Serialize(new { __typename = "Story", post_id = "personal", creation_time = 1700000000,
            message = new { text = "Our family outing" }, attachments = new[] { new { styles = new { attachment = new {
                target = target is null ? null : new { __typename = target }, media = new { __typename = mediaType },
                title_with_entities = new { text = "A memory or animated image" },
                story_attachment_link_renderer = new { attachment = new { web_link = new { url = "https://example.test/item" } } }
            } } } } });
        var post = Assert.Single(PayloadParser.Parse("facebook", raw).Posts).Post;
        Assert.Equal("Our family outing", post.Text); Assert.Null(post.SharedText); Assert.Null(post.SharedUrl);
    }
    [Fact]
    public void FacebookAuthoredSharedStoryTakesPrecedenceOverLinkPreview()
    {
        const string raw = """{"__typename":"Story","post_id":"share","creation_time":1700000000,"message":{"text":"My caption"},"attached_story":{"actors":[{"name":"Original Person"}],"message":{"text":"My own family news"},"wwwURL":"https://facebook.com/original/posts/1"},"attachments":[{"styles":{"attachment":{"title_with_entities":{"text":"Unrelated preview"},"story_attachment_link_renderer":{"attachment":{"web_link":{"url":"https://example.test/article"}}}}}}]}""";
        var post = Assert.Single(PayloadParser.Parse("facebook", raw).Posts).Post;
        Assert.Equal("My caption", post.Text); Assert.Equal("Original Person", post.SharedAuthor);
        Assert.Equal("My own family news", post.SharedText); Assert.Equal("https://facebook.com/original/posts/1", post.SharedUrl);
    }
    [Theory]
    [InlineData("https://video.example.test/watch/clip", true)]
    [InlineData("https://video.example.test/watch/clip", false)]
    [InlineData("javascript:alert(1)", true)]
    public void FacebookWebLinkWithoutTargetMarkerPreservesOnlyValidSharedContext(string url, bool title)
    {
        var attachment = new Dictionary<string, object?>
        {
            ["media"] = new { __typename = "GenericAttachmentMedia", large_share_image = new { uri = "https://cdn.example.test/preview.jpg", width = 500, height = 260 } },
            ["story_attachment_link_renderer"] = new { attachment = new { web_link = new { url } } }
        };
        if (title) attachment["title_with_entities"] = new { text = "Synthetic video title" };
        var raw = JsonSerializer.Serialize(new { __typename = "Story", post_id = "synthetic-link", creation_time = 1700000000,
            message = new { text = "Wow!" }, actors = new[] { new { id = "501", name = "Synthetic Person" } },
            attachments = new[] { new { styles = new { attachment } } } });
        var parsed = PayloadParser.Parse("facebook", raw);
        Assert.Empty(parsed.Diagnostics);
        var observation = Assert.Single(parsed.Posts);
        var post = observation.Post;
        Assert.Equal("Wow!", post.Text);
        Assert.Equal("image", Assert.Single(observation.Media).Kind);
        var valid = url.StartsWith("https://", StringComparison.Ordinal);
        Assert.Equal(valid ? url : null, post.SharedUrl);
        Assert.Equal(valid && title ? "Synthetic video title" : null, post.SharedText);
        Assert.Null(post.SharedAuthor);
        using var prompt = JsonDocument.Parse(Prompts.PostJson(post, new(null, [], new(new(), new(), Preferences.Parse(""))), [], []));
        Assert.Equal(valid, prompt.RootElement.TryGetProperty("shared", out var shared));
        if (valid)
        {
            Assert.Equal(url, shared.GetProperty("url").GetString());
            Assert.Equal("video.example.test", Assert.Single(prompt.RootElement.GetProperty("link_domains").EnumerateArray()).GetString());
        }
    }
    [Theory] [InlineData("cover photo")] [InlineData("profile picture")]
    public void FacebookStoryContextIsSeparateFromCaptionAndSharedContext(string update)
    {
        var title = "Synthetic Person updated their " + update + ".";
        var raw = """{"__typename":"Story","post_id":"synthetic","creation_time":1700000000,"actors":[{"id":"501","name":"Synthetic Person"}],"message":{"text":"Own caption"},"comet_sections":{"context_layout":{"story":{"comet_sections":{"title":{"story":{"title":{"text":"TITLE"}}}}}}},"attached_story":{"actors":[{"id":"601","name":"Other Person"}],"message":{"text":"Shared words"},"comet_sections":{"context_layout":{"story":{"comet_sections":{"title":{"story":{"title":{"text":"Other story context"}}}}}}}}}""".Replace("TITLE", title);
        var post = Assert.Single(PayloadParser.Parse("facebook", raw).Posts).Post;
        Assert.Equal(title, post.StoryTitle); Assert.Equal("Own caption", post.Text); Assert.Equal("Shared words", post.SharedText);
        var snapshot = new InstanceSnapshot(new(), new(), Preferences.Parse(""));
        using var body = JsonDocument.Parse(Prompts.PostJson(post, new(null, [], snapshot), [], []));
        Assert.Equal(title, body.RootElement.GetProperty("platform_context").GetString());
        Assert.Equal("Own caption", body.RootElement.GetProperty("text").GetString());
        var withoutOwnTitle = raw.Replace("\"text\":\"" + title + "\"", "\"text\":null");
        Assert.Null(Assert.Single(PayloadParser.Parse("facebook", withoutOwnTitle).Posts).Post.StoryTitle);
    }
    [Fact]
    public void FacebookTextOnlySharedStoryKeepsCaptionAndOriginalAuthorSeparate()
    {
        const string raw = """{"__typename":"Story","post_id":"share","creation_time":1700000000,"actors":[{"id":"501","name":"Sharing Person"}],"message":{"text":"My caption"},"comet_sections":{"content":{"story":{"attached_story":{"actors":[{"id":"601","name":"Original Person"}],"message":{"text":"Original text"},"wwwURL":"https://facebook.com/original/posts/1"}}}}}""";
        var post = Assert.Single(PayloadParser.Parse("facebook", raw).Posts);
        Assert.Equal("501", post.AuthorKey); Assert.Equal("My caption", post.Post.Text);
        Assert.Equal("Original Person", post.Post.SharedAuthor); Assert.Equal("Original text", post.Post.SharedText);
        Assert.Equal("https://facebook.com/original/posts/1", post.Post.SharedUrl);
        Assert.Single(PayloadParser.Parse("facebook", raw.Replace("\"message\":{\"text\":\"My caption\"},", "")).Posts);
    }
    [Fact]
    public void FacebookGroupedPostsKeepTheirOwnAuthorDateAndMediaWithoutAnAggregateDuplicate()
    {
        const string raw = """{"__typename":"Story","post_id":"aggregate","creation_time":1800000000,"actors":[{"id":"999"}],"comet_sections":{"content":{"story":{"comet_sections":{"aggregated_stories":{"__typename":"CometStoryAggregatedStoriesStrategy","story":{"interesting_substories":{"edges":[{"node":{"post_id":"child","actors":[{"id":"501","name":"Fixture"}],"message":{"text":"Synthetic birthday post"},"permalink_url":"https://facebook.com/fixture/posts/child","attachments":[{"image":{"uri":"https://cdn.example.test/child.jpg"}}],"comet_sections":{"context_layout":{"story":{"comet_sections":{"metadata":[{"story":{"creation_time":1700000000}}]}}}}}}],"remaining_count":0}}}}}}}}""";
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics);
        var post = Assert.Single(parsed.Posts); Assert.Equal("child", post.Post.PlatformPostId); Assert.Equal("501", post.AuthorKey);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime, post.Post.PostedAt);
        Assert.Equal("Synthetic birthday post", post.Post.Text); Assert.Equal("https://cdn.example.test/child.jpg", Assert.Single(post.Media).Url);
        Assert.Empty(PayloadParser.Parse("facebook", raw.Replace("\"creation_time\":1700000000", "\"unknown_time\":1700000000")).Posts);
    }
    [Fact]
    public void FacebookUnavailableSharesAreRecognizedWithoutClaimingAnEmptyTimeline()
    {
        const string story = """{"__typename":"Story","post_id":"unavailable","creation_time":1700000000,"attachments":[{"styles":{"__typename":"StoryAttachmentUnavailableStyleRenderer"}}]}""";
        var body = "{\"data\":{\"node\":{\"__typename\":\"User\",\"id\":\"fixture\",\"timeline_list_feed_units\":{\"edges\":[{\"node\":" + story + "}]}}}}\n"
            + "{\"path\":[\"node\",\"timeline_list_feed_units\",\"edges\",1],\"data\":{\"node\":" + story + ",\"cursor\":\"fixture\"}}\n"
            + "{\"path\":[\"node\",\"timeline_list_feed_units\"],\"data\":{\"page_info\":{\"has_next_page\":false}}}";
        var parsed = PayloadParser.Parse("facebook", body); Assert.Empty(parsed.Posts); Assert.Empty(parsed.Diagnostics); Assert.False(parsed.ExplicitEmpty);
        Assert.NotEmpty(PayloadParser.Parse("facebook", body.Replace("StoryAttachmentUnavailableStyleRenderer", "UnknownRenderer")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook", body.Replace("{\"data\":", "{\"errors\":[{}],\"data\":")).Diagnostics);
    }
    [Fact]
    public void FacebookProgressiveDeliverySelectsOnePlayableHdVideoAndIgnoresManifests()
    {
        const string raw = """{"__typename":"Story","post_id":"101","creation_time":1700000000,"message":{"text":"Synthetic video"},"url":"https://facebook.com/fixture/posts/101","attachments":[{"video":{"videoDeliveryResponseFragment":{"videoDeliveryResponseResult":{"progressive_urls":[{"progressive_url":"https://cdn.example.test/sd.mp4","failure_reason":null,"metadata":{"quality":"SD"}},{"progressive_url":"https://cdn.example.test/hd.mp4","failure_reason":null,"metadata":{"quality":"HD"}}],"dash_manifest_urls":["https://cdn.example.test/dash.mpd"]}}}}]}""";
        var post = Assert.Single(PayloadParser.Parse("facebook", raw).Posts);
        Assert.Equal("https://cdn.example.test/hd.mp4", Assert.Single(post.Media).Url);
        var failedHd = raw.Replace("\"failure_reason\":null,\"metadata\":{\"quality\":\"HD\"}", "\"failure_reason\":\"unavailable\",\"metadata\":{\"quality\":\"HD\"}");
        Assert.Equal("https://cdn.example.test/sd.mp4", Assert.Single(Assert.Single(PayloadParser.Parse("facebook", failedHd).Posts).Media).Url);
        var manifestsOnly = raw.Replace("\"progressive_urls\"", "\"unknown_urls\"");
        Assert.Equal("permalink:101", Assert.Single(Assert.Single(PayloadParser.Parse("facebook", manifestsOnly).Posts).Media).SourceKey);
    }
    [Theory]
    [InlineData("{\"data\":{\"xdt_api__v1__feed__user_timeline_graphql_connection\":{\"edges\":[],\"page_info\":{\"has_next_page\":false}}}}", true)]
    [InlineData("{\"data\":{\"xdt_api__v1__feed__user_timeline_graphql_connection\":{\"edges\":[],\"page_info\":{\"has_next_page\":true}}}}", false)]
    [InlineData("{\"errors\":[{}],\"data\":{\"xdt_api__v1__feed__user_timeline_graphql_connection\":{\"edges\":[],\"page_info\":{\"has_next_page\":false}}}}", false)]
    [InlineData("{\"data\":{\"unrelated_connection\":{\"edges\":[],\"page_info\":{\"has_next_page\":false}}}}", false)]
    public void InstagramProfileConnectionRequiresExplicitTerminalEmptyEvidence(string body, bool empty)
    {
        var parsed = PayloadParser.Parse("instagram", body);
        Assert.Empty(parsed.Posts); Assert.Equal(empty, parsed.ExplicitEmpty);
        Assert.Equal(empty, parsed.Diagnostics.Count == 0);
    }
    [Fact] public void InstagramGraphqlJsonMayUseJavascriptContentType()
    {
        Assert.True(Site.Gate("instagram", "https://www.instagram.com/graphql/query", "POST", 200, "text/javascript; charset=utf-8", "fb_api_req_friendly_name=PolarisProfilePostsQuery", false));
        Assert.False(Site.Gate("instagram", "https://www.instagram.com/graphql/query", "POST", 200, "text/html", null, false));
        Assert.False(Site.Gate("instagram", "https://example.test/graphql/query", "POST", 200, "text/javascript", null, false));
        Assert.False(Site.Gate("instagram", "https://www.instagram.com/graphql/query", "POST", 200, "text/javascript", null, false));
    }
    [Theory]
    [InlineData("PolarisFeedTimelineRootV2Query", true)]
    [InlineData("PolarisProfilePostsQuery", true)]
    [InlineData("PolarisAPIGetFrCookieQuery", false)]
    [InlineData("IGDBadgeCountOffMsysQuery", false)]
    [InlineData("PolarisProfileNoteBubbleQuery", false)]
    [InlineData("PolarisStoriesV3TrayContainerQuery", false)]
    [InlineData("QuickPromotionSupportIGSchemaBatchFetchQuery", false)]
    public void InstagramGraphqlCaptureExcludesUnrelatedBackgroundOperations(string operation, bool capture)
        => Assert.Equal(capture, Site.Gate("instagram", "https://www.instagram.com/graphql/query", "POST", 200, "application/json", "fb_api_req_friendly_name=" + operation, false));
    [Fact] public void TimelineCoverageExcludesBackgroundHomeFeedPosts()
    {
        var author = new Author { Platform = "instagram", RefsJson = "[\"ig:101\",\"ig:fixture\"]" };
        var own = new Observation(new() { Platform = "instagram", PlatformPostId = "one" }, "101", "Fixture", "https://www.instagram.com/fixture/", []);
        var other = own with { AuthorKey = "202", AuthorUrl = "https://www.instagram.com/other/" };
        Assert.True(Site.TimelineAuthorMatches("instagram", "https://www.instagram.com/fixture/", author, own));
        Assert.False(Site.TimelineAuthorMatches("instagram", "https://www.instagram.com/fixture/", author, other));
        Assert.True(Site.TimelineAuthorMatches("instagram", "https://www.instagram.com/fixture/", null, own));
        Assert.False(Site.TimelineAuthorMatches("instagram", "https://www.instagram.com/fixture/", null, other));
    }
    [Theory]
    [InlineData("{\"data\":{\"viewer\":{\"actor\":{},\"feed_comet_composer\":{}}}}")]
    [InlineData("{\"data\":{\"node\":{\"__typename\":\"GroupsYouShouldJoinFeedUnit\",\"items\":[]}}}")]
    [InlineData("{\"data\":{\"node\":{\"__typename\":\"PaginatedPeopleYouMayKnowFeedUnit\",\"items\":[]}}}")]
    [InlineData("{\"path\":[\"viewer\",\"news_feed\"],\"data\":{\"page_info\":{\"has_next_page\":false}}}")]
    [InlineData("{\"path\":[\"user\",\"timeline_list_feed_units\"],\"data\":{\"page_info\":{\"has_next_page\":false}}}")]
    [InlineData("{\"data\":{\"node\":{\"__typename\":\"User\",\"id\":\"fixture\",\"profile_tile_sections\":{}}}}")]
    public void KnownFacebookAuxiliaryResponsesAreNotPostsOrEmptyTimelineEvidence(string body)
    {
        var parsed = PayloadParser.Parse("facebook", body);
        Assert.Empty(parsed.Posts); Assert.Empty(parsed.Diagnostics); Assert.False(parsed.ExplicitEmpty);
    }
    [Theory]
    [InlineData("{\"data\":{\"node\":{\"__typename\":\"UnknownFeedUnit\"}}}")]
    [InlineData("{\"errors\":[],\"data\":{\"node\":{\"__typename\":\"GroupsYouShouldJoinFeedUnit\"}}}")]
    public void UnknownOrErroredFacebookResponsesRemainDiagnostic(string body) => Assert.NotEmpty(PayloadParser.Parse("facebook", body).Diagnostics);
    [Theory]
    [InlineData("CometAudioLanguageUtils_dubtrackMapping", "dubbed_track_mapping")]
    [InlineData("VideoPlayerWithLiveVideoEndscreenAndChaining_video", "live_end_text")]
    [InlineData("VideoPlayerWithVideoCardsOverlay_video", "comet_video_player_with_video_card_renderer")]
    [InlineData("CometFeedStoryVideoAttachmentScreenOverlay_video", "can_viewer_share")]
    [InlineData("InstreamVideoAdBreaksPlayer_video", "instream_video_ad_breaks_comet")]
    public void KnownTimelinePlayerMetadataIsAuxiliaryButUnexpectedFieldsRemainDiagnostic(string label, string field)
    {
        var raw = System.Text.Json.JsonSerializer.Serialize(new { label = "Fixture$defer$" + label, path = new object[] { "user", "timeline_list_feed_units", "edges", 0, "node", "media" }, data = new Dictionary<string, object?> { [field] = null } });
        var parsed = PayloadParser.Parse("facebook", raw); Assert.Empty(parsed.Diagnostics); Assert.Empty(parsed.Posts); Assert.False(parsed.ExplicitEmpty);
        Assert.Empty(PayloadParser.Parse("facebook", raw.Replace("\"user\"", "\"node\"")).Diagnostics);
        Assert.Empty(PayloadParser.Parse("facebook", raw.Replace("\"timeline_list_feed_units\",\"edges\",0,\"node\"", "\"profile_pinned_post\",\"pinned_post_story\"")).Diagnostics);
        Assert.Empty(PayloadParser.Parse("facebook", raw.Replace("\"user\",\"timeline_list_feed_units\"", "\"viewer\",\"news_feed\"")).Diagnostics);
        Assert.Empty(PayloadParser.Parse("facebook", raw.Replace("\"media\"]", "\"media\",\"video_grid_renderer\",\"video\"]")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace(field, "unknown_fixture_field")).Diagnostics);
        Assert.NotEmpty(PayloadParser.Parse("facebook", raw.Replace("timeline_list_feed_units", "unknown_surface")).Diagnostics);
    }
    [Theory][InlineData("/api/v1/feed/timeline/",true)][InlineData("/api/v1/media/1/like/",false)][InlineData("/api/v1/direct/inbox/",false)][InlineData("/api/v1/clips/user/",true)]public void InstagramCaptureGate(string path,bool expected)=>Assert.Equal(expected,Site.Gate("instagram","https://www.instagram.com"+path,"POST",200,"application/json",null,false));
    [Fact]public void FacebookFriendlyNameQueryPrecedesForm()=>Assert.True(Site.Gate("facebook","https://www.facebook.com/api/graphql/?fb_api_req_friendly_name=CometNewsFeedQuery","POST",200,"application/json","fb_api_req_friendly_name=Other",false));
    [Fact]public void FacebookFactsAndCodePointTagsStaySeparate()
    {
        const string raw="""{"data":{"story":{"__typename":"Story","post_id":"101","creation_time":1700000000,"actors":[{"id":"501","name":"Synthetic Person","url":"https://facebook.com/synthetic"}],"message":{"text":"😀 Synthetic Friend text","ranges":[{"offset":2,"length":16,"entity":{"__typename":"User","url":"https://facebook.com/fixture.friend"}}]},"feedback":{"id":"feedback101"},"wwwURL":"/synthetic/posts/101","sponsored_data":null,"tracking":"{\"originated_from_recommendation\":\"1\"}","attachments":[{"image":{"uri":"https://cdn.example.test/photo.jpg","width":640,"height":480}},{"image":{"uri":"https://static.example.test/rsrc.php/v4/glyph.png","width":100,"height":100}}]}}}""";
        var parsed=PayloadParser.Parse("facebook",raw);var p=Assert.Single(parsed.Posts);Assert.False(p.Post.IsSponsored);Assert.True(p.Post.IsSuggested);Assert.Equal("https://www.facebook.com/synthetic/posts/101",p.Post.Permalink);Assert.Equal("Synthetic Friend",Assert.Single(Filters.Tags(p.Post)).Name);Assert.Single(p.Media);
    }
    [Fact]public void EmbeddedFeedPayloadIsExtractedAndOtherPreloadersIgnored()
    {
        var html="<script type=\"application/json\">[[\"CometNewsFeed\",{\"__bbox\":{\"result\":{\"data\":{\"synthetic\":true}}}}],[\"Other\",{\"__bbox\":{\"result\":{\"data\":{}}}}]]</script>";Assert.Single(PayloadParser.Embedded(html));
    }
    [Fact]public void InstagramExplicitEmptyAndOrganicAdKeys()
    {
        Assert.True(PayloadParser.Parse("instagram","{\"user_timeline\":{\"edges\":[],\"page_info\":{\"has_next_page\":false}}}").ExplicitEmpty);
        var parsed=PayloadParser.Parse("instagram","{\"pk\":\"1\",\"code\":\"synthetic\",\"user\":{\"username\":\"synthetic\"},\"is_paid_partnership\":false,\"ad_id\":0,\"ad_id_latest\":null}");Assert.False(Assert.Single(parsed.Posts).Post.IsSponsored);
        Assert.Empty(PayloadParser.Parse("instagram","{\"pk\":\"1\",\"media_type\":2,\"user\":{\"username\":\"synthetic\"}}").Posts);
    }

    [Theory][InlineData("facebook","facebook.jsonl")][InlineData("instagram","instagram.json")]
    public void SanitizedFixtureFilesKeepMediaAndDeduplicatePosts(string platform,string file)
    {
        var body=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures",file));
        var parsed=PayloadParser.Parse(platform,body);var post=Assert.Single(parsed.Posts);Assert.Empty(parsed.Diagnostics);Assert.NotEmpty(post.Media);Assert.Contains("synthetic",post.Post.Text);Assert.NotNull(post.Post.PostedAt);
    }
}
