using System.Text.Json;
using System.Text.RegularExpressions;
using Feed.Core.Domain;
namespace Feed.Core.Infrastructure;

public sealed record ParsedCapture(IReadOnlyList<Observation> Posts, IReadOnlyList<Person> People, bool ExplicitEmpty, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}
public static class PayloadParser
{
    // Advance when a parser change can recover previously rejected immutable snapshots.
    public const int Version = 8;
    public static JsonElement At(JsonElement e, params string[] path) { foreach (var key in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return default; } return e; }
    public static string? Text(JsonElement e) => e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), JsonValueKind.Object => Text(At(e, "text")), _ => null };
    public static string? Get(JsonElement e, params string[] keys) => keys.Select(k => Text(At(e, k))).FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
    public static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    public static IEnumerable<(JsonElement Node, JsonElement[] Parents)> Walk(JsonElement e, JsonElement[]? parents = null)
    {
        parents ??= []; if (e.ValueKind == JsonValueKind.Object) { yield return (e, parents); foreach (var p in e.EnumerateObject()) foreach (var n in Walk(p.Value, [.. parents, e])) yield return n; }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var child in e.EnumerateArray()) foreach (var n in Walk(child, parents)) yield return n;
    }
    static bool Truth(JsonElement e) => e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n != 0 || e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && s != "0";
    static DateTime? Date(string? value) => long.TryParse(value, out var n) && n > 0 && n < 253402300800 ? DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime : null;
    static string? Url(string? s, string platform) { if (s is null) return null; if (s.StartsWith('/')) s = Platforms.Home(platform).TrimEnd('/') + s; return Uri.TryCreate(s, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri.ToString() : null; }
    public static string SourceKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var transient = new[] { "oh", "oe", "_nc_ohc", "_nc_sid", "_nc_ht", "_nc_cat", "_nc_oc", "_nc_eui2", "ccb", "se", "sig", "signature", "expires" };
        var query = u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Where(x => !transient.Contains(x.Split('=')[0], StringComparer.OrdinalIgnoreCase));
        return new UriBuilder(u) { Query = string.Join('&', query), Fragment = "" }.Uri.ToString();
    }
    public static ParsedCapture Parse(string platform, string body, bool people = false)
    {
        var roots = new List<JsonElement>(); var errors = new List<string>(); var warnings = new List<string>();
        try { using var d = JsonDocument.Parse(body); roots.Add(d.RootElement.Clone()); }
        catch (JsonException) { foreach (var line in body.Split('\n', StringSplitOptions.RemoveEmptyEntries)) { try { using var d = JsonDocument.Parse(line.StartsWith("for (;;);") ? line[9..] : line); roots.Add(d.RootElement.Clone()); } catch (JsonException e) { errors.Add(e.Message); } } }
        var posts = new Dictionary<string, Observation>(); var persons = new List<Person>(); var empty = false;
        foreach (var root in roots)
        {
            var damaged = new List<JsonElement>();
            foreach (var error in Array(At(root, "errors")))
            {
                var path = string.Join('/', Array(At(error, "path")).Select(Text));
                var detail = $"{platform}/GraphQL: {Get(error, "message") ?? "upstream error"}; path={path}";
                if (NonPostError(platform, root, error)) warnings.Add(detail);
                else { errors.Add(detail); damaged.Add(ErrorScope(platform, root, error)); }
            }
            var nodes = Walk(root).ToArray(); var children = nodes.SelectMany(x => Array(At(x.Node, "carousel_media"))).Select(Id).Where(s => s is not null).ToHashSet();
            foreach (var (n, parents) in nodes)
            {
                if (damaged.Any(scope => scope.Equals(n) || parents.Contains(scope))) continue;
                foreach (var key in platform == "instagram" ? new[] { "user_timeline", "xdt_api__v1__feed__user_timeline_graphql_connection" } : new[] { "user_timeline" })
                    if (At(n, key) is var timeline && timeline.ValueKind == JsonValueKind.Object && At(timeline, "edges").ValueKind == JsonValueKind.Array && !Array(At(timeline, "edges")).Any() && At(timeline, "page_info", "has_next_page").ValueKind == JsonValueKind.False && At(root, "errors").ValueKind == JsonValueKind.Undefined) empty = true;
                if (people) { var person = PersonFrom(platform, n, parents); if (person is not null) persons.Add(person); continue; }
                var observation = platform == "instagram" ? Instagram(n, parents, children) : Facebook(n, parents);
                if (observation is not null)
                {
                    // A Facebook wall can contain posts by somebody other than its
                    // owner. Attribute coverage through the enclosing profile response,
                    // without changing who authored the post or granting filter bypasses.
                    var owners = platform == "facebook" ? new[] { At(root, "data", "user"), At(root, "data", "node") }
                        .Where(p => Get(p, "id") is not null && parents.Contains(At(p, "timeline_list_feed_units")))
                        .Select(p => Get(p, "id")!).ToArray() : observation.TimelineOwnerIds.ToArray();
                    if (posts.TryGetValue(observation.Post.PlatformPostId, out var existing))
                        posts[observation.Post.PlatformPostId] = existing with { TimelineOwnerIds = existing.TimelineOwnerIds.Concat(owners).Distinct().ToArray() };
                    else posts.Add(observation.Post.PlatformPostId, observation with { TimelineOwnerIds = owners });
                }
            }
        }
        if (!people && posts.Count == 0 && !empty && !(platform == "facebook" && roots.Count > 0 && roots.All(FacebookAuxiliary))) errors.Add("parser uncertainty: no recognized posts or explicit empty response");
        return new(posts.Values.ToArray(), persons.DistinctBy(p => (p.Id, p.Url)).ToArray(), empty, errors) { Warnings = warnings };
    }
    static JsonElement ErrorScope(string platform, JsonElement root, JsonElement error)
    {
        var path = Array(At(error, "path")).Select(Text).ToArray();
        int edges = platform == "facebook" && path.Length > 3 && path[0] is "node" or "user" && path[1] == "timeline_list_feed_units" && path[2] == "edges" ? 2
            : platform == "instagram" && path.Length > 2 && path[0] is "xdt_api__v1__feed__timeline__connection" or "xdt_api__v1__feed__user_timeline_graphql_connection" && path[1] == "edges" ? 1 : -1;
        if (edges >= 0 && int.TryParse(path[edges + 1], out var index) && index >= 0)
        {
            var array = At(root, new[] { "data" }.Concat(path.Take(edges + 1).Select(p => p!)).ToArray());
            if (array.ValueKind == JsonValueKind.Array && index < array.GetArrayLength()) return array[index];
        }
        return root;
    }
    static bool FacebookAuxiliaryPostError(JsonElement root, string?[] path)
    {
        // Only the exact comment-attachment and rich-text entity metadata paths are optional.
        var nodePath = path.Length > 5 && path[0] is "node" or "user" && path[1] == "timeline_list_feed_units" && path[2] == "edges" && int.TryParse(path[3], out _) && path[4] == "node" ? 5
            : path.Length > 1 && path[0] == "node" ? 1 : 0;
        if (nodePath == 0) return false;
        var node = At(root, "data");
        foreach (var part in path.Take(nodePath))
        {
            if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < node.GetArrayLength()) node = node[index];
            else node = At(node, part!);
        }
        if (Get(node, "post_id") is null || At(node, "actors").ValueKind != JsonValueKind.Array) return false;
        var rest = path.Skip(nodePath).ToArray();
        if (rest.Length == 10 && rest.Take(7).SequenceEqual(new[] { "comet_sections", "feedback", "story", "story_ufi_container", "story", "feedback_context", "interesting_top_level_comments" })
            && int.TryParse(rest[7], out var comment) && comment >= 0 && rest[8] == "comment" && rest[9] == "attached_story") return true;
        var messagePath = new[] { "comet_sections", "content", "story", "comet_sections", "message", "story", "message" };
        if (rest.Length != 10 || !rest.Take(7).SequenceEqual(messagePath) || rest[7] != "ranges" || rest[9] != "entity" || !int.TryParse(rest[8], out var range) || range < 0) return false;
        var message = At(node, messagePath); var ranges = At(message, "ranges"); var text = Get(message, "text");
        if (text is null || ranges.ValueKind != JsonValueKind.Array || range >= ranges.GetArrayLength()) return false;
        return int.TryParse(Get(ranges[range], "offset"), out var offset) && int.TryParse(Get(ranges[range], "length"), out var length)
            && offset >= 0 && length >= 0 && offset <= text.Length && length <= text.Length - offset;
    }
    static bool NonPostError(string platform, JsonElement root, JsonElement error)
    {
        var path = Array(At(error, "path")).Select(Text).ToArray();
        if (platform == "facebook")
            return FacebookAuxiliaryPostError(root, path) || FacebookProfileTiles(root) && path.Length > 2 && path[0] == "node" && path[1] == "profile_tile_sections"
                || path.Length == 3 && path[0] is "user" or "node" && path[1] == "delegate_page"
                && path[2] is "ctx_business_adoption_fact_based_benchmark_page_id" or "ctwa_ad4ad_insights"
                && At(root, "data", path[0]!, "timeline_list_feed_units", "edges").ValueKind == JsonValueKind.Array;
        if (platform == "instagram" && path.Length == 5 && path[0] == "xdt_api__v1__feed__timeline__connection" && path[1] == "edges"
            && int.TryParse(path[2], out var edgeIndex) && edgeIndex >= 0 && path[3] == "node"
            && path[4] is "ad" or "ad4ad_in_webfeed" or "explore_story" or "end_of_feed_demarcator" or "stories_netego" or "suggested_users" or "bloks_netego" or "abra_icebreakers_in_feed_unit")
        {
            var edge = Array(At(root, "data", path[0]!, "edges")).Skip(edgeIndex).FirstOrDefault();
            var media = At(edge, "node", "media");
            if (Get(media, "pk", "id") is not null && At(media, "user").ValueKind == JsonValueKind.Object) return true;
        }
        // Instagram also reports a place-icon failure through an internal query alias.
        // Only the observed single-explore-media shape is known: index zero belongs
        // to that branch, not necessarily to edge zero in the outer timeline.
        if (platform == "instagram" && path.Length == 5
            && path[0] == "_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_explore_story_media"
            && path[1] == "0" && path[2] == "node" && path[3] == "location" && path[4] == "profile_pic_url")
        {
            var candidates = Array(At(root, "data", "xdt_api__v1__feed__timeline__connection", "edges"))
                .Select(e => At(e, "node", "explore_story", "media")).Where(m => m.ValueKind == JsonValueKind.Object).ToArray();
            if (candidates.Length != 1) return false;
            var media = candidates[0];
            return Get(media, "pk", "id") is not null && Get(media, "code", "shortcode") is not null
                && Get(At(media, "user"), "pk", "id", "username") is not null
                && At(media, "location", "profile_pic_url").ValueKind == JsonValueKind.Null;
        }
        // A place's icon is neither post media nor author/content. The exact observed
        // GraphQL path is required; errors on caption, user, images or edges still fail.
        return platform == "instagram" && path.Length == 6 && path[0] == "xdt_api__v1__feed__user_timeline_graphql_connection"
            && path[1] == "edges" && int.TryParse(path[2], out var index) && index >= 0 && path[3] == "node" && path[4] == "location" && path[5] == "profile_pic_url"
            && Array(At(root, "data", path[0]! , "edges")).Skip(index).Any();
    }
    static bool FacebookProfileTiles(JsonElement root)
    {
        var data = At(root, "data"); var node = At(data, "node");
        return data.ValueKind == JsonValueKind.Object && data.EnumerateObject().All(p => p.Name == "node")
            && Get(node, "__typename") == "User" && At(node, "profile_tile_sections").ValueKind == JsonValueKind.Object
            && node.EnumerateObject().All(p => p.Name is "__typename" or "id" or "profile_tile_sections");
    }
    static bool FacebookAuxiliary(JsonElement root)
    {
        // Observed non-post feed responses, not proof of an empty timeline. Keep unknown
        // shapes diagnostic; only these explicit composer/pagination/group suggestions qualify.
        if (At(root, "errors").ValueKind != JsonValueKind.Undefined && !(Array(At(root, "errors")).Any() && Array(At(root, "errors")).All(e => NonPostError("facebook", root, e)))) return false;
        var data = At(root, "data");
        if (data.ValueKind != JsonValueKind.Object) return false;
        var viewer = At(data, "viewer");
        if (At(viewer, "feed_comet_composer").ValueKind == JsonValueKind.Object && viewer.EnumerateObject().All(p => p.Name is "actor" or "feed_comet_composer")) return true;
        if (Get(At(data, "node"), "__typename") is "GroupsYouShouldJoinFeedUnit" or "PaginatedPeopleYouMayKnowFeedUnit") return true;
        var node = At(data, "node");
        if (Get(node, "__typename") == "User" && At(node, "profile_tile_sections").ValueKind == JsonValueKind.Object && node.EnumerateObject().All(p => p.Name is "__typename" or "id" or "profile_tile_sections")) return true;
        var path = Array(At(root, "path")).Select(Text).ToArray();
        if ((path.SequenceEqual(new[] { "viewer", "news_feed" }) || path.Length == 2 && path[0] is "user" or "node" && path[1] == "timeline_list_feed_units") && At(data, "page_info").ValueKind == JsonValueKind.Object && data.EnumerateObject().All(p => p.Name == "page_info")) return true;
        foreach (var key in new[] { "user", "node" })
        {
            var profile = At(data, key); var edges = Array(At(profile, "timeline_list_feed_units", "edges")).ToArray();
            if (edges.Length > 0 && data.EnumerateObject().All(p => p.Name == key) && At(profile, "profile_pinned_post").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && edges.All(e => UnavailableFacebookStory(At(e, "node")))) return true;
        }
        if (path.Length == 4 && path[0] is "user" or "node" && path[1] == "timeline_list_feed_units" && path[2] == "edges" && data.EnumerateObject().All(p => p.Name is "node" or "cursor") && UnavailableFacebookStory(node)) return true;
        // Deferred player controls accompany the already captured parent story; they
        // aren't independent posts or evidence that an otherwise empty timeline loaded.
        if (path.Length < 3 || !(path[0] is "user" or "node" && (path[1] == "timeline_list_feed_units" || path[1] == "profile_pinned_post" && path[2] == "pinned_post_story") || path[0] == "viewer" && path[1] == "news_feed" && path[2] == "edges") || !(path[^1] == "media" || path.TakeLast(3).SequenceEqual(new[] { "media", "video_grid_renderer", "video" })) || data.ValueKind != JsonValueKind.Object) return false;
        var label = Get(root, "label")?.Split("$defer$").Last();
        string[] fields = label switch
        {
            "CometAudioLanguageUtils_dubtrackMapping" => ["dubbed_track_mapping"],
            "VideoPlayerWithLiveVideoEndscreenAndChaining_video" => ["comet_video_player_live_video_endscreen_content", "live_end_text", "is_huddle", "is_live_audio_room_v2_broadcast", "associated_paid_online_event", "id", "is_live_streaming", "is_paid_virtual_event_premium_content"],
            "VideoPlayerWithVideoCardsOverlay_video" => ["comet_video_player_with_video_card_renderer"],
            "CometFeedStoryVideoAttachmentScreenOverlay_video" => ["can_viewer_share", "creation_story", "end_cards_channel_info", "is_soundbites_video", "is_looping", "info"],
            "InstreamVideoAdBreaksPlayer_video" => ["id", "instream_extra_config", "instream_video_ad_breaks_comet"],
            _ => []
        };
        return fields.Length > 0 && data.EnumerateObject().Any() && data.EnumerateObject().All(p => fields.Contains(p.Name));
    }
    static bool UnavailableFacebookStory(JsonElement n)
    {
        var attachments = Array(At(n, "attachments")).ToArray();
        return Get(n, "__typename") == "Story" && Get(n, "post_id", "id") is not null && Date(Get(n, "creation_time")) is not null
            && Get(n, "message", "text") is null && Get(At(n, "comet_sections", "content", "story"), "message") is null
            && At(n, "attached_story").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            && attachments.Length > 0 && attachments.All(a => Get(At(a, "styles"), "__typename") == "StoryAttachmentUnavailableStyleRenderer");
    }
    static string? Id(JsonElement n) => Get(n, "pk", "id", "media_id", "shortcode", "code");
    static Observation? Instagram(JsonElement n, JsonElement[] parents, HashSet<string?> children)
    {
        var code = Get(n, "code", "shortcode", "media_code", "pk_string"); var id = Id(n); if (code is null || id is null || children.Contains(id)) return null;
        var user = At(n, "user"); if (user.ValueKind != JsonValueKind.Object) user = At(n, "owner"); var handle = Get(user, "username") ?? Get(n, "username"); var uid = Get(user, "pk", "id") ?? Get(n, "user_id", "owner") ?? handle;
        if (uid is null && !new[] { "image_versions2", "display_resources", "display_url", "thumbnail_src", "video_versions", "carousel_media", "media_type" }.Any(k => At(n, k).ValueKind != JsonValueKind.Undefined)) return null;
        var p = new Post { Platform = "instagram", PlatformPostId = id, LikeRef = id, PostedAt = Date(Get(n, "taken_at", "taken_at_timestamp", "timestamp", "published")), Text = Get(n, "caption", "text", "caption_text"), ObservedAuthorName = Get(user, "full_name") ?? handle, ObservedAuthorUrl = handle is null ? null : $"https://www.instagram.com/{handle}/" };
        var product = Get(n, "product_type"); p.IsReel = product is "clips" or "igtv" or "reel" || Get(n, "media_type") is "2" or "5";
        p.Permalink = Url(Get(n, "permalink", "share_url"), p.Platform) ?? $"https://www.instagram.com/{(p.IsReel ? "reel" : "p")}/{code}/"; p.IsReel |= p.Permalink.Contains("/reel/");
        p.IsSponsored = new[] { "is_paid_partnership", "paid_partener", "is_paid_sponsored", "ad_id_latest", "ad_id" }.Any(k => Truth(At(n, k))) || product == "ad";
        p.IsSuggested = parents.Append(n).Any(x => Truth(At(x, "suggested_for_you")) || new[] { "unit_title", "title", "feed_type", "type" }.Any(k => Suggested(Get(x, k))));
        var tags = Array(At(n, "coauthor_producers")).Concat(Array(At(n, "coauthors"))).Select(x => new Tag(Get(x, "full_name", "username") ?? "", Get(x, "username") is { } h ? $"https://www.instagram.com/{h}/" : null)).ToArray(); if (tags.Length > 0) p.TagsJson = JsonSerializer.Serialize(tags, InstanceValidation.Json);
        var slides = Array(At(n, "carousel_media")).ToArray(); if (slides.Length == 0) slides = [n]; var media = new List<MediaSource>();
        foreach (var slide in slides) if (Image(slide) is { } image) media.Add(new("image", image, SourceKey(image)));
        if (media.Count == 0 && Image(n) is { } fallback) media.Add(new("image", fallback, SourceKey(fallback)));
        foreach (var slide in slides) if (Array(At(slide, "video_versions")).Select(v => Get(v, "url")).FirstOrDefault(x => x is not null) is { } video) media.Add(new("video", video, SourceKey(video)));
        if (p.IsReel && media.All(x => x.Kind != "video")) media.Add(new("video", p.Permalink, "permalink:" + id));
        return new(p, uid, p.ObservedAuthorName, p.ObservedAuthorUrl, Limit(media, 10))
        { TimelineOwnerIds = Array(At(n, "coauthor_producers")).Concat(Array(At(n, "coauthors"))).Select(x => Get(x, "pk", "id")).OfType<string>().Distinct().ToArray() };
    }
    static bool Suggested(string? s) => s is not null && (new[] { "discover_media", "discover_top_media", "explore_grid_media", "feed_backtracking_unit", "feed_follow_requests", "feed_suggestions_unit", "media_with_liked_by", "top_reels_media", "suggested_for_you", "suggested_for_you_unit" }.Contains(s) || s.Contains("suggested for you", StringComparison.OrdinalIgnoreCase) || s.Contains("for you page", StringComparison.OrdinalIgnoreCase));
    static string? Image(JsonElement n)
    {
        var candidates = Array(At(n, "image_versions2", "candidates")).ToArray(); if (candidates.Length == 0) candidates = Array(At(n, "display_resources")).ToArray();
        int Width(JsonElement c) => int.TryParse(Get(c, "width", "config_width"), out var w) ? w : 0;
        var choice = candidates.Where(x => Width(x) > 0 && Width(x) <= 1080).OrderByDescending(Width).FirstOrDefault();
        if (choice.ValueKind == JsonValueKind.Undefined) choice = candidates.Where(x => Width(x) > 0).OrderBy(Width).FirstOrDefault();
        if (choice.ValueKind == JsonValueKind.Undefined) choice = candidates.FirstOrDefault();
        return Get(choice, "url", "src") ?? Get(n, "thumbnail_src", "display_url");
    }
    static Observation? Facebook(JsonElement n, JsonElement[] parents)
    {
        var substory = parents.Length >= 4 && Get(parents[^4], "__typename") == "CometStoryAggregatedStoriesStrategy"
            && At(parents[^4], "story").Equals(parents[^3]) && At(parents[^3], "interesting_substories").Equals(parents[^2]) && At(parents[^1], "node").Equals(n);
        if (!substory && !(Get(n, "__typename") ?? "").Contains("Story", StringComparison.Ordinal)) return null;
        var id = Get(n, "post_id", "id"); if (id is null || id == "0") return null;
        bool Sponsored(JsonElement x) => new[] { "sponsored_data", "th_dat_spo" }.Any(k => At(x, k).ValueKind == JsonValueKind.Object && At(x, k).EnumerateObject().Any()) || At(x, "ad_id").ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null) || (Get(x, "__typename") ?? "").Contains("sponsored", StringComparison.OrdinalIgnoreCase);
        var sponsored = parents.Concat(Walk(n).Select(x => x.Node)).Any(Sponsored); var time = Date(Get(n, "creation_time"));
        if (time is null && substory) time = Array(At(n, "comet_sections", "context_layout", "story", "comet_sections", "metadata")).Select(m => Date(Get(At(m, "story"), "creation_time"))).FirstOrDefault(t => t is not null);
        if (time is null && !sponsored) return null;
        var actor = Array(At(n, "actors")).FirstOrDefault(); var authorUrl = Url(Get(actor, "wwwURL", "url"), "facebook");
        var shared = new[] { At(n, "attached_story"), At(n, "comet_sections", "content", "story", "attached_story") }.Concat(Walk(At(n, "attachments")).Select(x => x.Node)).FirstOrDefault(x => At(x, "actors").ValueKind == JsonValueKind.Array && Get(x, "message") is not null);
        var caption = Get(n, "message") ?? Get(At(n, "comet_sections", "content", "story"), "message") ?? Get(n, "text");
        var p = new Post { Platform = "facebook", PlatformPostId = id, Text = caption, PostedAt = time, IsSponsored = sponsored, ObservedAuthorName = Get(actor, "name"), ObservedAuthorUrl = authorUrl, LikeRef = Get(At(n, "feedback"), "id", "targetID"), Permalink = Url(Get(n, "wwwURL", "permalink", "permalink_url", "url"), "facebook"), SharedAuthor = Get(Array(At(shared, "actors")).FirstOrDefault(), "name"), SharedText = Get(shared, "message"), SharedUrl = Url(Get(shared, "wwwURL", "permalink_url"), "facebook") };
        p.StoryTitle = Get(At(n, "comet_sections", "context_layout", "story", "comet_sections", "title", "story"), "title");
        // Link shares may have captions and preview images. Their title/source belong
        // to the shared block, never to the friend's own words.
        if (shared.ValueKind == JsonValueKind.Undefined)
        {
            var link = Array(At(n, "attachments")).Select(a => At(a, "styles", "attachment"))
                .FirstOrDefault(a => (Get(At(a, "target"), "__typename") == "ExternalUrl" && Get(a, "title_with_entities") is not null)
                    || Get(At(a, "target"), "__typename") is null or "ExternalUrl"
                        && Get(At(a, "media"), "__typename") is null or "GenericAttachmentMedia"
                        && Url(Get(At(a, "story_attachment_link_renderer", "attachment", "web_link"), "url"), "facebook") is not null);
            p.SharedText = Get(link, "title_with_entities"); p.SharedAuthor = Get(link, "source");
            p.SharedUrl = Url(Get(At(link, "story_attachment_link_renderer", "attachment", "web_link"), "url"), "facebook");
        }
        p.IsReel = p.Permalink?.Contains("/reel/") == true || p.Permalink?.Contains("/reels/") == true; p.IsEvent = p.Permalink?.Contains("/events/") == true;
        p.IsSuggested = parents.Concat(Walk(n).Select(x => x.Node)).Any(x => (Get(At(x, "badge"), "text") ?? "").Contains("suggested", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(Get(x, "tracking") ?? "", "\"originated_from_recommendation\"\\s*:\\s*\"?1"));
        var media = new List<MediaSource>(); bool videoHint = false;
        void MediaWalk(JsonElement x)
        {
            if (x.ValueKind == JsonValueKind.Array) { foreach (var a in x.EnumerateArray()) MediaWalk(a); return; } if (x.ValueKind != JsonValueKind.Object) return;
            var image = Get(At(x, "image"), "uri") ?? (At(x, "width").ValueKind != JsonValueKind.Undefined || At(x, "height").ValueKind != JsonValueKind.Undefined ? Get(x, "uri") : null);
            if (image is not null && !image.Contains("/rsrc.php/")) media.Add(new("image", image, SourceKey(image)));
            var progressive = Array(At(x, "videoDeliveryResponseFragment", "videoDeliveryResponseResult", "progressive_urls"))
                .Where(v => At(v, "failure_reason").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                .OrderByDescending(v => Get(At(v, "metadata"), "quality") == "HD")
                .Select(v => Url(Get(v, "progressive_url"), "facebook")).FirstOrDefault(v => v is not null);
            if ((Get(x, "browser_native_hd_url", "browser_native_sd_url", "playable_url") ?? progressive) is { } video) { media.Add(new("video", video, SourceKey(video))); videoHint = true; }
            foreach (var property in x.EnumerateObject()) { if (property.Name == "video") videoHint = true; if (!new[] { "actors", "feedback", "badge", "profile_picture", "profilePicture", "interesting_substories" }.Contains(property.Name)) MediaWalk(property.Value); }
        }
        MediaWalk(n); if (videoHint && media.All(x => x.Kind != "video") && p.Permalink is not null) media.Add(new("video", p.Permalink, "permalink:" + id));
        if (string.IsNullOrWhiteSpace(p.Text) && string.IsNullOrWhiteSpace(p.SharedText) && media.Count == 0) return null;
        var tags = new List<Tag>();
        foreach (var (block, _) in Walk(n))
        {
            if ((Get(block, "__typename") ?? "").Contains("Throwback", StringComparison.OrdinalIgnoreCase)) { p.MemoryLabel = Get(block, "title"); p.MemoryText = Get(At(block, "target"), "message"); }
            var text = Get(block, "text"); if (text is null) continue; var runes = text.EnumerateRunes().ToArray();
            var withStart = text.IndexOf(" is with ", StringComparison.Ordinal); var atEnd = text.LastIndexOf(" at ", StringComparison.Ordinal);
            foreach (var range in Array(At(block, "ranges")))
            {
                var entity = At(range, "entity"); if (Get(entity, "__typename") != "User") continue;
                if (!int.TryParse(Get(range, "offset"), out var offset) || !int.TryParse(Get(range, "length"), out var length) || offset < 0 || length < 1 || offset + length > runes.Length) continue;
                var name = string.Concat(runes.Skip(offset).Take(length).Select(x => x.ToString()));
                var utfOffset = string.Concat(runes.Take(offset).Select(x => x.ToString())).Length;
                tags.Add(new(name, Url(Get(entity, "url", "profile_url"), "facebook"), withStart >= 0 && utfOffset >= withStart + 9 && (atEnd < withStart || utfOffset < atEnd) ? "with" : null));
            }
            if ((Get(block, "__typename") ?? "").Contains("Throwback", StringComparison.OrdinalIgnoreCase)) { p.MemoryLabel = Get(block, "title"); p.MemoryText = Get(At(block, "target"), "message"); }
        }
        if (tags.Count > 0) p.TagsJson = JsonSerializer.Serialize(tags.DistinctBy(t => (t.Name, t.Url)), InstanceValidation.Json);
        var aid = Get(actor, "id", "idString", "__dr") ?? Identity.UrlRef("facebook", authorUrl)?[3..];
        return new(p, aid, p.ObservedAuthorName, authorUrl, Limit(media, 8));
    }
    static MediaSource[] Limit(List<MediaSource> media, int limit) => media.Where(x => x.Kind == "image").DistinctBy(x => x.SourceKey).Take(limit).Concat(media.Where(x => x.Kind == "video").Take(1)).ToArray();
    static Person? PersonFrom(string platform, JsonElement n, JsonElement[] parents)
    {
        if (platform == "instagram") { var handle = Get(n, "username"); if (handle is null || Get(n, "code", "shortcode") is not null || parents.Any(x => Get(x, "code", "shortcode") is not null)) return null; return new(Get(n, "pk", "id") ?? handle, Get(n, "full_name") ?? handle, $"https://www.instagram.com/{handle}/", Get(n, "profile_pic_url_hd", "profile_pic_url")); }
        var hint = Get(n, "type") == "FRIEND"; var name = Get(n, "title"); var url = Url(Get(n, "url", "link_url"), platform); if (name is null || url is null || !(hint || At(n, "profile_picture").ValueKind == JsonValueKind.Object || (Get(n, "__typename") ?? "").Contains("User"))) return null;
        var reference = Identity.UrlRef(platform, url); if (reference is null) return null;
        var numeric = new[] { reference[3..], Get(n, "userID", "id", "idString", "__dr") }.FirstOrDefault(x => long.TryParse(x, out _));
        return new(numeric ?? Get(n, "ent_id") ?? reference[3..], name, url, Get(n, "img_url") ?? Get(At(n, "profile_picture"), "uri"));
    }
    public static IEnumerable<string> Embedded(string html)
    {
        foreach (Match match in Regex.Matches(html, "<script[^>]*type=[\"']application/json[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            JsonElement root; try { using var doc = JsonDocument.Parse(match.Groups[1].Value); root = doc.RootElement.Clone(); } catch (JsonException) { continue; }
            foreach (var payload in EmbeddedWalk(root)) yield return payload;
        }
    }
    static IEnumerable<string> EmbeddedWalk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { var a = e.EnumerateArray().ToArray(); if (a.Length == 2 && Text(a[0])?.Contains("Feed", StringComparison.OrdinalIgnoreCase) == true && At(a[1], "__bbox", "result", "data").ValueKind == JsonValueKind.Object) yield return At(a[1], "__bbox", "result").GetRawText(); foreach (var child in a) foreach (var p in EmbeddedWalk(child)) yield return p; }
        if (e.ValueKind == JsonValueKind.Object) foreach (var property in e.EnumerateObject()) foreach (var p in EmbeddedWalk(property.Value)) yield return p;
    }
}
