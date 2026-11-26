using System.Text.Json;
using System.Text.RegularExpressions;
using Feed.Core.Domain;
namespace Feed.Core.Infrastructure;

public sealed record ParsedCapture(IReadOnlyList<Observation> Posts, IReadOnlyList<Person> People, bool ExplicitEmpty, IReadOnlyList<string> Diagnostics)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<CapturePart> Parts { get; init; } = [];
    public IReadOnlyList<CaptureDiagnostic> Issues { get; init; } = [];
}
public static partial class PayloadParser
{
    // Advance when a parser change can recover previously rejected immutable snapshots.
    public const int Version = 17;
    [GeneratedRegex("\"originated_from_recommendation\"\\s*:\\s*\"?1")] private static partial Regex RecommendationRegex();
    [GeneratedRegex("<script[^>]*type=[\"']application/json[\"'][^>]*>(.*?)</script>", RegexOptions.Singleline | RegexOptions.IgnoreCase)] private static partial Regex ScriptJsonRegex();

    public static JsonElement At(JsonElement e, string k1) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k1, out var v) ? v : default;
    public static JsonElement At(JsonElement e, string k1, string k2) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k1, out var v1) && v1.ValueKind == JsonValueKind.Object && v1.TryGetProperty(k2, out var v2) ? v2 : default;
    public static JsonElement At(JsonElement e, string k1, string k2, string k3) => At(At(e, k1, k2), k3);
    public static JsonElement At(JsonElement e, params string[] path) { foreach (var key in path) { if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(key, out e)) return default; } return e; }
    public static string? Text(JsonElement e) => e.ValueKind switch { JsonValueKind.String => e.GetString(), JsonValueKind.Number => e.GetRawText(), JsonValueKind.Object => Text(At(e, "text")), _ => null };
    public static string? Get(JsonElement e, string k1) { var s = Text(At(e, k1)); return string.IsNullOrWhiteSpace(s) ? null : s; }
    public static string? Get(JsonElement e, string k1, string k2) => Get(e, k1) ?? Get(e, k2);
    public static string? Get(JsonElement e, string k1, string k2, string k3) => Get(e, k1) ?? Get(e, k2) ?? Get(e, k3);
    public static string? Get(JsonElement e, string k1, string k2, string k3, string k4) => Get(e, k1) ?? Get(e, k2) ?? Get(e, k3) ?? Get(e, k4);
    public static string? Get(JsonElement e, params string[] keys)
    {
        foreach (var k in keys) { var s = Text(At(e, k)); if (!string.IsNullOrWhiteSpace(s)) return s; }
        return null;
    }
    public static IEnumerable<JsonElement> Array(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];
    public static IEnumerable<(JsonElement Node, JsonElement[] Parents)> Walk(JsonElement e, JsonElement[]? parents = null)
    {
        var result = new List<(JsonElement, JsonElement[])>();
        var stack = new List<JsonElement>(16);
        if (parents is not null) stack.AddRange(parents);
        void Visit(JsonElement node)
        {
            if (node.ValueKind == JsonValueKind.Object)
            {
                result.Add((node, stack.Count == 0 ? [] : stack.ToArray()));
                stack.Add(node);
                foreach (var p in node.EnumerateObject())
                    if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(p.Value);
                stack.RemoveAt(stack.Count - 1);
            }
            else if (node.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in node.EnumerateArray())
                    if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array) Visit(child);
            }
        }
        Visit(e);
        return result;
    }
    internal static bool HasPostIdOrShortcode(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (Get(e, "post_id", "shortcode") is not null) return true;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && HasPostIdOrShortcode(p.Value)) return true;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in e.EnumerateArray())
                if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array && HasPostIdOrShortcode(child)) return true;
        }
        return false;
    }
    static bool Truth(JsonElement e) => e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var n) && n != 0 || e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s && s != "0";
    static DateTime? Date(string? value) => long.TryParse(value, out var n) && n >= -62135596800 && n < 253402300800 && n != 0 ? DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime : null;
    static string? Url(string? s, string platform)
    {
        if (s is null) return null;
        if (s.StartsWith('/')) s = Platforms.Home(platform).TrimEnd('/') + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https")) return null;
        // Fast path avoids UriBuilder allocation when the URL is already canonical https on the default port.
        if (u.Scheme == "https" && u.IsDefaultPort && u.Fragment.Length == 0 && u.Query != "?") return u.ToString();
        return new UriBuilder(u) { Scheme = "https", Port = -1 }.Uri.ToString();
    }
    static string? NonBlankText(JsonElement e) { var s = Text(e); return string.IsNullOrWhiteSpace(s) ? null : s; }
    static bool HasAnyProperty(JsonElement obj) { foreach (var _ in obj.EnumerateObject()) return true; return false; }
    // Media identity, never a fetch URL. Meta CDN links rotate most query parameters per request
    // (signatures, cache ids, delivery hints such as _nc_gid or efg), and also the host and the rendition
    // (stp): on 2026-11-10 the live instance held 1,216 groups of one asset under several keys, 948 of them
    // host rotations. Each one re-identified an unchanged picture: a new download, a new content revision,
    // a new model verdict. Meta names an asset once (<n>_<n>_<n>_n.jpg, <token>.mp4), so on a Meta host the
    // filename is the identity. A proxied external preview (url=) is identified by its origin, and any other
    // URL keeps scheme, host, path and the stp/url parameters. Changing this rule needs a key version bump
    // in MediaKeys so stored rows and content hashes are normalized once instead of churning.
    public static string SourceKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
        var path = u.AbsolutePath;
        var file = path[(path.LastIndexOf('/') + 1)..];
        var rawQuery = u.Query;
        // Fast path for Meta CDN links without a proxied `url=` query parameter: avoids query splitting and UriBuilder.
        if (file.Contains('.') && (u.Host.EndsWith(".fbcdn.net", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".cdninstagram.com", StringComparison.OrdinalIgnoreCase)))
        {
            if (rawQuery.Length <= 1 || !HasQueryParam(rawQuery.AsSpan(1), "url")) return "file:" + file;
        }
        var query = rawQuery.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
        var kept = query.Where(x => { int eq = x.IndexOf('='); var key = eq < 0 ? x : x[..eq]; return key.Equals("stp", StringComparison.OrdinalIgnoreCase) || key.Equals("url", StringComparison.OrdinalIgnoreCase); }).Order(StringComparer.Ordinal);
        return new UriBuilder(u) { Query = string.Join('&', kept), Fragment = "" }.Uri.ToString();
    }
    static bool HasQueryParam(ReadOnlySpan<char> query, ReadOnlySpan<char> name)
    {
        while (!query.IsEmpty)
        {
            int amp = query.IndexOf('&');
            var part = amp < 0 ? query : query[..amp];
            int eq = part.IndexOf('=');
            var key = eq < 0 ? part : part[..eq];
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase)) return true;
            if (amp < 0) break;
            query = query[(amp + 1)..];
        }
        return false;
    }
    static bool HasMoreThanOneProperty(JsonElement obj)
    {
        int count = 0;
        foreach (var _ in obj.EnumerateObject()) if (++count > 1) return true;
        return false;
    }
    static bool LooksLikeJsonl(string body)
        => body.StartsWith("for (;;);", StringComparison.Ordinal)
            || body.Contains("}\n{", StringComparison.Ordinal)
            || body.Contains("}\r\n{", StringComparison.Ordinal)
            || body.Contains("}\nfor (;;);", StringComparison.Ordinal)
            || body.Contains("}\r\nfor (;;);", StringComparison.Ordinal);
    public static ParsedCapture Parse(string platform, string body, bool people = false, CaptureContext? context = null)
    {
        var observations = new Dictionary<string, Observation>(); var persons = new List<Person>();
        var parts = new List<CapturePart>(); var issues = new List<CaptureDiagnostic>(); var errors = new List<string>(); var warnings = new List<string>();

        void ProcessRoot(int index, JsonElement root, string? parseError)
        {
            ParsedCapture parsed;
            HashSet<string>? postDataBranches = null;
            if (parseError is not null)
            {
                parsed = new([], [], false, [parseError]);
            }
            else
            {
                var data = At(root, "data");
                bool checkSiblings = !people && data.ValueKind == JsonValueKind.Object && HasMoreThanOneProperty(data);
                if (checkSiblings) postDataBranches = new HashSet<string>(StringComparer.Ordinal);
                parsed = ParseRoot(platform, root, people, postDataBranches);
                // Inspect independent data siblings as well as JSONL roots. A successful
                // post branch cannot establish that an unfamiliar sibling is harmless.
                if (checkSiblings && parsed.Posts.Count > 0)
                {
                    foreach (var branch in data.EnumerateObject())
                    {
                        if (branch.Value.ValueKind is JsonValueKind.Null or JsonValueKind.False || EnvelopeMetadata(platform, branch)) continue;
                        // A branch that already produced a post in ParseRoot is recognized; skip re-serializing and re-parsing it.
                        if (postDataBranches!.Contains(branch.Name)) continue;
                        var wrapped = JsonSerializer.Serialize(new { data = new Dictionary<string, JsonElement> { [branch.Name] = branch.Value } });
                        using var siblingDoc = JsonDocument.Parse(wrapped);
                        var sibling = ParseRoot(platform, siblingDoc.RootElement, false, null);
                        if (sibling.Posts.Count == 0 && !sibling.ExplicitEmpty && sibling.Diagnostics.Count > 0)
                        {
                            var message = "parser uncertainty: unrecognized data branch; path=data/" + branch.Name;
                            parsed = parsed with { Diagnostics = parsed.Diagnostics.Append(message).ToArray() };
                        }
                    }
                }
            }
            foreach (var o in parsed.Posts)
                if (observations.TryGetValue(o.Post.PlatformPostId, out var old)) observations[o.Post.PlatformPostId] = (old.IsPartial && !o.IsPartial ? o : old) with { TimelineOwnerIds = old.TimelineOwnerIds.Concat(o.TimelineOwnerIds).Distinct().ToArray() };
                else observations.Add(o.Post.PlatformPostId, o);
            persons.AddRange(parsed.People); errors.AddRange(parsed.Diagnostics); warnings.AddRange(parsed.Warnings);
            parts.Add(new(index, parsed.Posts.Count > 0 ? "posts" : parsed.ExplicitEmpty ? "explicit_empty" : parsed.Diagnostics.Count == 0 ? "auxiliary" : "unknown", parsed.Posts.Count, parsed.ExplicitEmpty));
            foreach (var m in parsed.Diagnostics) issues.Add(CaptureDiagnostics.Create(platform, context?.Operation, root, index, m, false));
            foreach (var m in parsed.Warnings) issues.Add(CaptureDiagnostics.Create(platform, context?.Operation, root, index, m, true));
        }

        // Parse each JSON root once without throwing JsonException on normal compact JSONL captures or cloning DOM trees.
        bool parsedAsSingle = false;
        if (!LooksLikeJsonl(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                ProcessRoot(0, doc.RootElement, null);
                parsedAsSingle = true;
            }
            catch (JsonException) { }
        }
        if (!parsedAsSingle)
        {
            var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length == 0) lines = [body];
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.StartsWith("for (;;);", StringComparison.Ordinal)) line = line[9..];
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    ProcessRoot(index, doc.RootElement, null);
                }
                catch (JsonException e)
                {
                    ProcessRoot(index, default, e.Message);
                }
            }
        }
        return new(observations.Values.ToArray(), persons.DistinctBy(p => (p.Id, p.Url)).ToArray(), parts.Any(p => p.ExplicitEmpty), errors) { Warnings = warnings, Parts = parts, Issues = issues };
    }
    static bool EnvelopeMetadata(string platform, JsonProperty branch)
    {
        var value = branch.Value;
        static bool Scalar(JsonElement e) => e.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null;
        if (platform == "instagram" && branch.Name == "xdt_viewer")
            return value.ValueKind == JsonValueKind.Object && value.EnumerateObject().All(p => p.Name == "user")
                && Get(At(value, "user"), "id") is not null && At(value, "user").EnumerateObject().All(p => p.Name == "id");
        if (platform != "facebook") return false;
        if (branch.Name is "cursor" or "deduplication_key" or "min_gap_type_index_string" or "sposnsor_new_distac_action" or "top_story_cache_score" or "brs_content_label" or "is_ad_eligible_for_ad_pod") return Scalar(value);
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (branch.Name == "cat_sensitive")
            return value.EnumerateObject().Any() && value.EnumerateObject().All(p => p.Name is "enc" or "cat" && Scalar(p.Value));
        if (branch.Name == "feed_backend_data")
            return value.EnumerateObject().Any() && value.EnumerateObject().All(p => p.Name is "qid" or "original_qid" or "vsid" or "vspos" or "should_recent_vpv_fetch_tracking_data" or "serialized_payloads" && Scalar(p.Value));
        return false;
    }
    static ParsedCapture ParseRoot(string platform, JsonElement root, bool people, HashSet<string>? postDataBranches)
    {
        var errors = new List<string>(); var warnings = new List<string>();
        var posts = new Dictionary<string, Observation>(); var persons = new List<Person>(); var empty = false;
        var damaged = new List<JsonElement>(); var partial = new List<JsonElement>();
        bool hasRootErrors = At(root, "errors").ValueKind != JsonValueKind.Undefined;
        if (hasRootErrors)
        {
            foreach (var error in Array(At(root, "errors")))
            {
                var path = string.Join('/', Array(At(error, "path")).Select(Text));
                var detail = $"{platform}/GraphQL: {Get(error, "message") ?? "upstream error"}; path={path}";
                if (NonPostError(platform, root, error)) warnings.Add(detail);
                else if (platform == "instagram" && InstagramPartialError(root, error, out var media))
                {
                    warnings.Add(detail + "; partial metadata unavailable (including link context where reported); usable caption/author/media retained, existing stored post protected");
                    partial.Add(media);
                }
                else { errors.Add(detail); damaged.Add(ErrorScope(platform, root, error)); }
            }
        }
        bool isInstagram = platform == "instagram";
        HashSet<string?>? children = null;
        if (isInstagram && !people)
        {
            children = new HashSet<string?>();
            void CollectCarouselChildren(JsonElement e)
            {
                if (e.ValueKind == JsonValueKind.Object)
                {
                    if (e.TryGetProperty("carousel_media", out var cm) && cm.ValueKind == JsonValueKind.Array)
                        foreach (var item in cm.EnumerateArray()) if (Id(item) is { } cid) children.Add(cid);
                    foreach (var p in e.EnumerateObject())
                        if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) CollectCarouselChildren(p.Value);
                }
                else if (e.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in e.EnumerateArray())
                        if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array) CollectCarouselChildren(item);
                }
            }
            CollectCarouselChildren(root);
        }

        // Precompute Facebook profile timeline owners once per root instead of re-reading At(root, "data", "user"/"node") per story.
        (JsonElement Units, string OwnerId)[] fbOwners = [];
        if (!isInstagram && !people)
        {
            var data = At(root, "data");
            if (data.ValueKind == JsonValueKind.Object)
            {
                var u = At(data, "user"); var n = At(data, "node");
                var list = new List<(JsonElement, string)>(2);
                if (Get(u, "id") is { } uid && At(u, "timeline_list_feed_units") is { ValueKind: not JsonValueKind.Undefined } uu) list.Add((uu, uid));
                if (Get(n, "id") is { } nid && At(n, "timeline_list_feed_units") is { ValueKind: not JsonValueKind.Undefined } nu) list.Add((nu, nid));
                if (list.Count > 0) fbOwners = list.ToArray();
            }
        }

        var parentStack = new List<JsonElement>(24);
        static bool ContainsElement(List<JsonElement> scopes, JsonElement n, List<JsonElement> parents)
        {
            foreach (var scope in scopes)
            {
                if (scope.Equals(n)) return true;
                for (int i = 0; i < parents.Count; i++) if (parents[i].Equals(scope)) return true;
            }
            return false;
        }
        static bool CheckEmptyTimeline(JsonElement n, ReadOnlySpan<byte> key)
            => n.TryGetProperty(key, out var timeline) && timeline.ValueKind == JsonValueKind.Object
                && timeline.TryGetProperty("edges"u8, out var edges) && edges.ValueKind == JsonValueKind.Array && edges.GetArrayLength() == 0
                && timeline.TryGetProperty("page_info"u8, out var pi) && pi.ValueKind == JsonValueKind.Object
                && pi.TryGetProperty("has_next_page"u8, out var next) && next.ValueKind == JsonValueKind.False;

        void WalkNode(JsonElement n, string? dataBranch, bool inData)
        {
            if (n.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in n.EnumerateArray())
                    if (child.ValueKind is JsonValueKind.Object or JsonValueKind.Array) WalkNode(child, dataBranch, false);
                return;
            }
            if (n.ValueKind != JsonValueKind.Object) return;
            bool skip = damaged.Count > 0 && ContainsElement(damaged, n, parentStack);
            if (!skip)
            {
                if (!hasRootErrors && (CheckEmptyTimeline(n, "user_timeline"u8) || isInstagram && CheckEmptyTimeline(n, "xdt_api__v1__feed__user_timeline_graphql_connection"u8)))
                    empty = true;
                if (people)
                {
                    var person = PersonFrom(platform, n, parentStack);
                    if (person is not null) persons.Add(person);
                }
                else
                {
                    var observation = isInstagram ? Instagram(n, parentStack, children!) : Facebook(n, parentStack);
                    if (observation is not null)
                    {
                        if (dataBranch is not null) postDataBranches?.Add(dataBranch);
                        bool isPartial = partial.Count > 0 && ContainsElement(partial, n, parentStack);
                        if (isPartial) observation = observation with { IsPartial = true };
                        // A Facebook wall can contain posts by somebody other than its
                        // owner. Attribute coverage through the enclosing profile response,
                        // without changing who authored the post or granting filter bypasses.
                        IReadOnlyList<string> owners;
                        if (!isInstagram)
                        {
                            if (fbOwners.Length == 0) owners = [];
                            else
                            {
                                List<string>? found = null;
                                foreach (var (units, oid) in fbOwners)
                                    for (int i = 0; i < parentStack.Count; i++)
                                        if (parentStack[i].Equals(units)) { (found ??= []).Add(oid); break; }
                                owners = found is null ? [] : found.ToArray();
                            }
                        }
                        else owners = observation.TimelineOwnerIds;
                        if (posts.TryGetValue(observation.Post.PlatformPostId, out var existing))
                            posts[observation.Post.PlatformPostId] = (existing.IsPartial && !observation.IsPartial ? observation : existing) with { TimelineOwnerIds = existing.TimelineOwnerIds.Concat(owners).Distinct().ToArray() };
                        else posts.Add(observation.Post.PlatformPostId, !isInstagram ? observation with { TimelineOwnerIds = owners } : observation);
                    }
                }
            }
            int depth = parentStack.Count;
            parentStack.Add(n);
            foreach (var prop in n.EnumerateObject())
            {
                if (prop.Value.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array)) continue;
                bool childInData = postDataBranches is not null && depth == 0 && prop.Name == "data";
                string? nextBranch = inData ? prop.Name : dataBranch;
                WalkNode(prop.Value, nextBranch, childInData);
            }
            parentStack.RemoveAt(depth);
        }

        WalkNode(root, null, false);
        if (!people && posts.Count == 0 && !empty)
        {
            // Known auxiliary roots pass in silence. Unknown deferred fragments without content pass with a
            // warning each (ContentFreeFragment). Any other root makes the whole response uncertain.
            if (!isInstagram && FacebookAuxiliary(root)) { }
            else if (!isInstagram && ContentFreeFragment(root))
                warnings.Add($"facebook/deferred: unrecognized fragment {Get(root, "label")!.Split("$defer$").Last()}; no post fields, links or text; path={string.Join('/', Array(At(root, "path")).Select(Text))}");
            else errors.Add("parser uncertainty: no recognized posts or explicit empty response");
        }
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
        if (platform == "instagram" && InstagramExploreBranchError(root, error)) return true;
        // The fragment aliases index their branches in response order, not outer timeline edges.
        // Explore alias: one slot per explore_story object; keep null media slots so malformed
        // branches cannot shift the index. Feed-item alias (observed 2026-10-16): one slot per
        // non-null node/media object, because the fragment is not applied to a null media.
        if (platform == "instagram" && path.Length == 5
            && path[0] is "_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_explore_story_media"
                or "_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_media"
            && int.TryParse(path[1], out var exploreIndex) && exploreIndex >= 0
            && path[2] == "node" && path[3] == "location" && path[4] == "profile_pic_url")
        {
            var edges = Array(At(root, "data", "xdt_api__v1__feed__timeline__connection", "edges"));
            var candidates = path[0]!.EndsWith("_explore_story_media")
                ? edges.Select(e => At(e, "node", "explore_story")).Where(e => e.ValueKind == JsonValueKind.Object).Select(e => At(e, "media")).ToArray()
                : edges.Select(e => At(e, "node", "media")).Where(e => e.ValueKind == JsonValueKind.Object).ToArray();
            if (exploreIndex >= candidates.Length) return false;
            var media = candidates[exploreIndex];
            return InstagramMediaUsable(media)
                && candidates.Count(m => Get(m, "pk", "id") == Get(media, "pk", "id")) == 1
                && At(media, "location", "profile_pic_url").ValueKind == JsonValueKind.Null;
        }
        if (platform == "instagram" && path.Length == 8 && path[0] == "xdt_api__v1__feed__timeline__connection"
            && path[1] == "edges" && int.TryParse(path[2], out var exploreEdge) && exploreEdge >= 0
            && path[3] == "node" && path[4] == "explore_story" && path[5] == "media"
            && path[6] == "location" && path[7] == "profile_pic_url")
            return InstagramMediaUsable(Follow(At(root, "data"), path.Take(6)))
                && Follow(At(root, "data"), path).ValueKind == JsonValueKind.Null;
        // A place's icon is neither post media nor author/content. The exact observed
        // GraphQL path is required; errors on caption, user, images or edges still fail.
        return platform == "instagram" && path.Length == 6 && path[0] == "xdt_api__v1__feed__user_timeline_graphql_connection"
            && path[1] == "edges" && int.TryParse(path[2], out var index) && index >= 0 && path[3] == "node" && path[4] == "location" && path[5] == "profile_pic_url"
            && Array(At(root, "data", path[0]! , "edges")).Skip(index).Any();
    }
    static JsonElement Follow(JsonElement node, IEnumerable<string?> path)
    {
        foreach (var part in path)
        {
            if (node.ValueKind == JsonValueKind.Array && int.TryParse(part, out var index) && index >= 0 && index < node.GetArrayLength()) node = node[index];
            else if (node.ValueKind == JsonValueKind.Object && part is not null) node = At(node, part);
            else return default;
        }
        return node;
    }
    static bool InstagramMediaUsable(JsonElement media)
    {
        if (Get(media, "pk", "id") is null || Get(media, "code", "shortcode") is null
            || Get(At(media, "user"), "pk", "id", "username") is null
            || !(At(media, "caption").ValueKind == JsonValueKind.Null || At(media, "caption", "text").ValueKind == JsonValueKind.String)) return false;
        static bool Asset(JsonElement item) => Uri.TryCreate(Image(item), UriKind.Absolute, out var image) && image.Scheme is "https" or "http"
            || Array(At(item, "video_versions")).Any(v => Uri.TryCreate(Get(v, "url"), UriKind.Absolute, out var video) && video.Scheme is "https" or "http");
        var slides = At(media, "carousel_media");
        if (slides.ValueKind == JsonValueKind.Array && slides.GetArrayLength() > 0)
            return int.TryParse(Get(media, "carousel_media_count"), out var count) && count == slides.GetArrayLength()
                && Array(slides).All(s => Get(s, "pk", "id") is not null && Asset(s));
        return (!int.TryParse(Get(media, "carousel_media_count"), out var expected) || expected == 0) && Asset(media);
    }
    static bool InstagramExploreBranchError(JsonElement root, JsonElement error)
    {
        var path = Array(At(error, "path")).Select(Text).ToArray();
        if (path.Length < 5 || path[0] != "xdt_api__v1__feed__timeline__connection" || path[1] != "edges"
            || !int.TryParse(path[2], out var index) || index < 0 || path[3] != "node"
            || Follow(At(root, "data"), path).ValueKind != JsonValueKind.Null) return false;
        var node = Follow(At(root, "data"), path.Take(4));
        if (!InstagramMediaUsable(At(node, "explore_story", "media"))) return false;
        return path.Length == 5 && path[4] is "media" or "ad" or "ad4ad_in_webfeed" or "end_of_feed_demarcator" or "stories_netego" or "suggested_users" or "bloks_netego" or "abra_icebreakers_in_feed_unit"
            || path.Length == 6 && path[4] == "explore_story" && path[5] == "ad";
    }
    static bool InstagramPartialError(JsonElement root, JsonElement error, out JsonElement media)
    {
        media = default;
        var path = Array(At(error, "path")).Select(Text).ToArray();
        if (path.Length < 6 || path[0] != "xdt_api__v1__feed__timeline__connection" || path[1] != "edges"
            || !int.TryParse(path[2], out var index) || index < 0 || path[3] != "node"
            || Follow(At(root, "data"), path).ValueKind != JsonValueKind.Null) return false;
        int start = path[4] == "media" ? 5 : path[4] == "explore_story" && path[5] == "media" ? 6 : 0;
        if (start == 0 || path.Length <= start) return false;
        media = Follow(At(root, "data"), path.Take(start));
        if (!InstagramMediaUsable(media)) return false;
        var rest = path.Skip(start).ToArray();
        if (rest.Length == 1) return rest[0] is "ai_interactive_embodiment_attachment_style_info" or "ai_label_info" or "audience" or "brs_severity"
            or "can_reshare" or "carousel_parent_id" or "follow_hashtag_info" or "headline" or "link" or "link_text" or "story_cta";
        if (rest.Length == 3 && rest[0] == "carousel_media" && int.TryParse(rest[1], out var slide) && slide >= 0)
            return rest[2] is "carousel_media" or "clips_metadata" or "code" or "headline" or "link" or "organic_tracking_token" or "story_cta" or "user";
        if (rest.Length == 3 && rest[0] == "coauthor_producers" && int.TryParse(rest[1], out var coauthor) && coauthor >= 0)
            return Get(Follow(media, rest.Take(2)), "pk", "id", "username") is not null && rest[2] is "aigm_account_label_info" or "is_unpublished" or "supervision_info";
        return rest.Length == 5 && rest[0] == "usertags" && rest[1] == "in" && int.TryParse(rest[2], out var tag) && tag >= 0
            && rest[3] == "user" && rest[4] == "aigm_account_label_info" && Get(Follow(media, rest.Take(4)), "pk", "id", "username") is not null;
    }
    static bool FacebookComposerWithPrivacy(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || data.EnumerateObject().Any(p => p.Name is not ("user" or "viewer" or "privacy_selector"))) return false;
        var user = At(data, "user");
        if (user.ValueKind != JsonValueKind.Undefined && (Get(user, "id") is null
            || user.EnumerateObject().Any(p => p.Name is not ("id" or "__isProfile" or "delegate_page" or "vcuDisableCometSfvRedirect"
                or "has_taggable_products" or "is_product_tagging_apux_snoozed" or "profile_plus_category_display_name" or "name"
                or "is_eligible_smcta_on_promode" or "has_professional_features_for_watch" or "has_profile_comet_mentions_tab"
                or "is_memorialized" or "posted_item_privacy_scope" or "short_name")) || ContainsPostContent(user))) return false;
        var viewer = At(data, "viewer"); var selector = At(data, "privacy_selector"); var composer = At(viewer, "feed_comet_composer");
        return viewer.ValueKind == JsonValueKind.Object && selector.ValueKind == JsonValueKind.Object
            && Get(At(viewer, "actor"), "id") is not null && At(composer, "sprouts").ValueKind == JsonValueKind.Array
            && composer.EnumerateObject().All(p => p.Name is "sprouts" or "__isICometComposer" or "__typename" or "auxiliary_footer_buttons" or "footer_primary_buttons")
            && viewer.EnumerateObject().All(p => p.Name is "actor" or "feed_comet_composer" or "eligible_promotions" or "has_outstanding_conflicts_for_privacy_merge"
                or "privacy_merge_metadata" or "privacy_conflict_ui_to_display" or "privacy_merge_conflict_resolution_suggested_selected_option" or "format_merge_milestone_for_user")
            && selector.EnumerateObject().All(p => p.Name is "privacy_scope_renderer" or "can_viewer_edit" or "privacy_write_id" or "show_quick_actions_menu")
            && Get(At(selector, "privacy_scope_renderer"), "__typename") is not null
            && At(selector, "privacy_scope_renderer", "privacy_row_input").ValueKind == JsonValueKind.Object
            && At(selector, "privacy_scope_renderer", "scope").ValueKind == JsonValueKind.Object;
    }
    static bool FacebookProfileTiles(JsonElement root)
    {
        var data = At(root, "data"); var node = At(data, "node");
        return data.ValueKind == JsonValueKind.Object && data.EnumerateObject().All(p => p.Name == "node")
            && Get(node, "__typename") == "User" && At(node, "profile_tile_sections").ValueKind == JsonValueKind.Object
            && node.EnumerateObject().All(p => p.Name is "__typename" or "id" or "profile_tile_sections");
    }
    static bool FacebookProfileHeader(JsonElement root)
    {
        var data = At(root, "data");
        if (data.ValueKind != JsonValueKind.Object) return false;
        var user = At(data, "user"); var header = At(user, "profile_header_renderer");
        if (At(root, "path").ValueKind == JsonValueKind.Undefined && At(root, "label").ValueKind == JsonValueKind.Undefined)
            return data.EnumerateObject().All(p => p.Name == "user") && Get(user, "id") is { } id
                && user.EnumerateObject().All(p => p.Name is "id" or "profile_header_renderer" or "profile_plus_post_transition_modal")
                && At(user, "profile_plus_post_transition_modal").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined
                && Get(header, "__typename") == "XFBProfileEntityConvergenceHeaderRenderer"
                && Get(At(header, "user"), "__typename") == "User" && Get(At(header, "user"), "id") == id
                && At(header, "user").EnumerateObject().All(p => p.Name is "__isProfile" or "name" or "profile_picture_for_sticky_bar"
                    or "__isEntity" or "__typename" or "url" or "gender" or "wem_private_sharing_bundle" or "is_viewer_friend"
                    or "user_avatar" or "__isRenderedProfile" or "cover_photo" or "avatar_cover_photo_takeover_type"
                    or "profile_active_messenger_room" or "story_bucket" or "is_additional_profile_plus" or "delegate_page" or "id"
                    or "live_video_for_comet_live_ring" or "msgr_user_rich_status" or "is_live_for_comet_live_ring" or "profilePhoto"
                    or "profilePicLarge" or "profilePicMedium" or "profilePicSmall" or "profile_video" or "profile_picture_expiration_time"
                    or "header_top_row" or "profile_tabs" or "alternate_name" or "username_for_profile" or "ai_profile_attribution_subtitle"
                    or "show_verified_badge_on_profile" or "is_visibly_memorialized" or "profile_social_context"
                    or "is_eligible_for_comet_floating_action_bar" or "top_banner" or "if_viewer_can_see_pymk_unit_on_initial_load")
                && header.EnumerateObject().All(p => p.Name is "__typename" or "user" or "viewer" or "nux" or "restriction_violation_status"
                    or "legal_reporting_uri" or "legal_reporting_cta_type" or "__module_operation_ProfileCometHeaderQuery" or "__module_component_ProfileCometHeaderQuery");
        var path = Array(At(root, "path")).Select(Text).ToArray();
        if (!path.SequenceEqual(new[] { "user", "profile_header_renderer", "user" })
            && !path.SequenceEqual(new[] { "user", "profile_header_renderer", "user", "header_top_row", "profile_user" })) return false;
        string[] fields = Get(root, "label") switch
        {
            "ProfileCometProfilePicture_user$defer$ProfileCometLockedProfilePopover_user" => ["name", "gender", "wem_private_sharing_bundle"],
            "ProfileCometProfilePicture_user$defer$ProfileCometProfilePictureViewMenu_user"
                or "ProfileCometProfilePicture_user$defer$ProfileCometProfilePictureEditMenu_user"
                or "ProfileCometProfilePicture_user$defer$ProfileCometProfilePictureTemporaryEditMenu_user" => ["story_bucket"],
            "ProfileCometProfilePicture_profile$defer$ProfileCometProfilePictureViewMenu_profile"
                or "ProfileCometProfilePicture_profile$defer$ProfileCometProfilePictureEditMenu_profile" => ["__isProfile", "profile_photo", "profilePic160", "profile_video", "profile_picture_is_silhouette"],
            "ProfileCometProfilePicture_profile$defer$ProfileCometProfilePictureTemporaryEditMenu_profile" => ["__isProfile", "profile_photo", "profile_picture_expiration_time", "profilePic160", "profile_video"],
            "ProfileCometDirectoryHeaderTopRow_renderer$defer$ProfileCometHeaderPrimaryActionBar_actor"
                or "ProfileCometEntityConvergenceHeader_profileHeaderRenderer$defer$ProfileCometHeaderPrimaryActionBar_actor" => ["__isActorWithActionBar", "is_viewer_eligible_for_directory_on_comet", "is_viewer_eligible_for_pymk_carat_in_comet_directory", "primaryActions", "__isNode", "__isHasRecommendationsUnit", "recommendations_unit"],
            "ProfileCometEntityConvergenceHeader_profileHeaderRenderer$defer$ProfileCometHeaderActionBar_actor"
                or "ProfileCometEntityConvergenceHeader_profileHeaderRenderer$defer$ProfileCometHeaderMoreActionButton_actor" => ["__isActorWithActionBar", "primaryActions", "secondaryActions", "is_scaled_category_view_enabled", "newMoreOptionsActions", "profile_plus_transition_path"],
            _ => []
        };
        return fields.Length > 0 && data.EnumerateObject().Any() && data.EnumerateObject().All(p => fields.Contains(p.Name));
    }
    static bool FacebookAuxiliary(JsonElement root)
    {
        // Observed non-post feed responses, not proof of an empty timeline. Keep unknown
        // shapes diagnostic; only these explicit composer/pagination/group suggestions qualify.
        if (At(root, "errors").ValueKind != JsonValueKind.Undefined && !(Array(At(root, "errors")).Any() && Array(At(root, "errors")).All(e => NonPostError("facebook", root, e)))) return false;
        var data = At(root, "data");
        if (data.ValueKind != JsonValueKind.Object) return false;
        if (FacebookProfileHeader(root)) return true;
        if (Get(At(data, "node"), "__typename") == "DiscoverFeedUnit" && Get(At(data, "cat_sensitive"), "cat") == "MULTI_FB_STORIES_TRAY"
            && data.EnumerateObject().All(p => p.Name == "node" || EnvelopeMetadata("facebook", p) || p.Value.ValueKind == JsonValueKind.Null)
            && At(data, "node", "content", "item_objects") is var trays && trays.ValueKind == JsonValueKind.Array && trays.GetArrayLength() > 0
            && Array(trays).All(t => Get(At(t, "data"), "__typename") == "DirectMessageThreadBucket" && Get(At(t, "data"), "__isStoryBucket") == "DirectMessageThreadBucket"
                && t.EnumerateObject().All(p => p.Name is "data" or "id"))
            && !HasAnyPostIdOrAttachments(data)) return true;
        var viewer = At(data, "viewer");
        if (FacebookComposerWithPrivacy(data)) return true;
        if (data.EnumerateObject().All(p => p.Name == "viewer") && At(viewer, "feed_comet_composer").ValueKind == JsonValueKind.Object && viewer.EnumerateObject().All(p => p.Name is "actor" or "feed_comet_composer")) return true;
        if (Get(At(data, "node"), "__typename") is "GroupsYouShouldJoinFeedUnit" or "PaginatedPeopleYouMayKnowFeedUnit") return true;
        // The first page of the home feed wraps the same suggestion units in viewer/news_feed/edges
        // beside envelope metadata; a feed page made only of them carries no post.
        var feed = At(viewer, "news_feed");
        if (data.EnumerateObject().All(p => p.Name == "viewer") && viewer.ValueKind == JsonValueKind.Object && viewer.EnumerateObject().All(p => p.Name == "news_feed")
            && feed.ValueKind == JsonValueKind.Object && feed.EnumerateObject().All(p => p.Name == "edges") && Array(At(feed, "edges")).ToArray() is { Length: > 0 } units
            && units.All(e => Get(At(e, "node"), "__typename") is "GroupsYouShouldJoinFeedUnit" or "PaginatedPeopleYouMayKnowFeedUnit" && e.EnumerateObject().All(p => p.Name == "node" || EnvelopeMetadata("facebook", p)) && !ContainsPostContent(e))) return true;
        var node = At(data, "node");
        if (Get(node, "__typename") == "User" && At(node, "profile_tile_sections").ValueKind == JsonValueKind.Object && node.EnumerateObject().All(p => p.Name is "__typename" or "id" or "profile_tile_sections")) return true;
        var path = Array(At(root, "path")).Select(Text).ToArray();
        // Deferred logging data for a story's "show more / show less" bumper button: scalars and
        // id lists for Facebook's own telemetry, never post content.
        if (TimelineEdge(path) && path.Length == 12 && path.Skip(5).SequenceEqual(new[] { "comet_sections", "call_to_action", "story", "bumpers", "persistent_bumper", "cta_renderer", "comet_action" })
            && Get(root, "label") == "CometShowMoreShowLessCallToAction_renderer$defer$useShowMoreShowLessCallToActionLogger_logData"
            && data.EnumerateObject().Any() && data.EnumerateObject().All(p => p.Name is "affected_display_names" or "affected_ids" or "bumper_class" or "bumper_scope" or "generator_uuid" or "ifr_show_rate_increase" or "show_rate_n" or "trigger_reason")
            && !ContainsPostContent(data)) return true;
        if ((path.SequenceEqual(new[] { "viewer", "news_feed" }) || path.Length == 2 && path[0] is "user" or "node" && path[1] == "timeline_list_feed_units") && At(data, "page_info").ValueKind == JsonValueKind.Object && data.EnumerateObject().All(p => p.Name == "page_info")) return true;
        foreach (var key in new[] { "user", "node" })
        {
            var profile = At(data, key); var edges = Array(At(profile, "timeline_list_feed_units", "edges")).ToArray();
            if (edges.Length > 0 && data.EnumerateObject().All(p => p.Name == key) && At(profile, "profile_pinned_post").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && edges.All(e => UnavailableFacebookStory(At(e, "node")) || ContentlessFacebookStory(At(e, "node")))) return true;
        }
        if (path.Length == 4 && path[0] is "user" or "node" && path[1] == "timeline_list_feed_units" && path[2] == "edges" && data.EnumerateObject().All(p => p.Name is "node" or "cursor") && (UnavailableFacebookStory(node) || ContentlessFacebookStory(node))) return true;
        // Deferred player controls accompany the already captured parent story; they
        // aren't independent posts or evidence that an otherwise empty timeline loaded.
        // The label and the field set identify such a fragment. The path only says where it attaches,
        // and that place changes with each container Facebook adds (a comment link, a message range,
        // an album grid, a memory collage). So there is no rule per path: any place under a story qualifies.
        if (!UnderStory(path) || ContainsPostContent(data)) return false;
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
    static bool TimelineEdge(string?[] p) => p.Length > 4 && (p[0] is "user" or "node" && p[1] == "timeline_list_feed_units" || p[0] == "viewer" && p[1] == "news_feed") && p[2] == "edges" && int.TryParse(p[3], out var n) && n >= 0 && p[4] == "node";
    // The path of a deferred fragment that hangs below a story: an edge node of a profile timeline or the home
    // feed, or the pinned post. A path that only names the timeline (not one of its edges) is not under a story.
    static bool UnderStory(string?[] p) => TimelineEdge(p) || p.Length >= 3 && p[0] is "user" or "node" && p[1] == "profile_pinned_post" && p[2] == "pinned_post_story";
    // An unknown deferred fragment under a story with no post fields, no links and no text. It cannot hold
    // a post or a part of one, so it must not block the capture: Facebook adds player and telemetry
    // fragments faster than an allowlist can follow. The caller records it as a warning, so a new shape
    // stays visible on the recovery page. A fragment with any content stays a diagnostic.
    static bool ContentFreeFragment(JsonElement root)
    {
        var data = At(root, "data");
        return At(root, "errors").ValueKind == JsonValueKind.Undefined && Get(root, "label") is { } label && label.Contains("$defer$", StringComparison.Ordinal)
            && UnderStory(Array(At(root, "path")).Select(Text).ToArray()) && data.ValueKind == JsonValueKind.Object
            && !ContainsPostContent(data) && !CarriesText(data);
    }
    // Ids, enum names and tracking blobs are ASCII without spaces. Captions and names have spaces or
    // non-ASCII letters, and media arrive as links. Facebook wraps display text as {"text": ...}, so a `text`
    // string counts even as one plain word. Only `data` is checked: `extensions` always holds prefetch links.
    static bool CarriesText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().Any(p => p.NameEquals("text") && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() != "" || CarriesText(p.Value)),
        JsonValueKind.Array => e.EnumerateArray().Any(CarriesText),
        JsonValueKind.String => e.GetString() is { } s && (s.StartsWith("http", StringComparison.OrdinalIgnoreCase) || s.Any(c => c > 127 || char.IsWhiteSpace(c))),
        _ => false
    };
    static bool UnavailableFacebookStory(JsonElement n)
    {
        var attachments = Array(At(n, "attachments")).ToArray();
        return Get(n, "__typename") == "Story" && Get(n, "post_id", "id") is not null && Date(Get(n, "creation_time")) is not null
            && Get(n, "message", "text") is null && Get(At(n, "comet_sections", "content", "story"), "message") is null
            && At(n, "attached_story").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            && attachments.Length > 0 && attachments.All(a => Get(At(a, "styles"), "__typename") == "StoryAttachmentUnavailableStyleRenderer");
    }
    // A dated wall post with no caption, no attachment and no media. Old "wrote on your wall"
    // entries arrive like this; the post builder drops them, so they are neither posts nor
    // evidence of an empty timeline. The builder call is the proof that nothing usable is inside.
    static bool ContentlessFacebookStory(JsonElement n)
        => Get(n, "__typename") == "Story" && Get(n, "post_id", "id") is not null && Date(Get(n, "creation_time")) is not null
            && Get(n, "message", "text") is null && Get(At(n, "comet_sections", "content", "story"), "message") is null
            && At(n, "attached_story").ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            && At(n, "attachments").ValueKind == JsonValueKind.Array && !Array(At(n, "attachments")).Any()
            && Facebook(n, []) is null;
    static bool ContainsPostContent(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in node.EnumerateObject())
            {
                if (p.NameEquals("post_id"u8) || p.NameEquals("message"u8) || p.NameEquals("caption"u8) || p.NameEquals("attachments"u8) || p.NameEquals("attached_story"u8) || p.NameEquals("timeline_list_feed_units"u8) || p.NameEquals("news_feed"u8)) return true;
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && ContainsPostContent(p.Value)) return true;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array && ContainsPostContent(item)) return true;
        }
        return false;
    }
    static bool HasAnyPostIdOrAttachments(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in node.EnumerateObject())
            {
                if (p.NameEquals("post_id"u8) || p.NameEquals("attachments"u8)) return true;
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array && HasAnyPostIdOrAttachments(p.Value)) return true;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in node.EnumerateArray())
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array && HasAnyPostIdOrAttachments(item)) return true;
        }
        return false;
    }
    static string? Id(JsonElement n) => Get(n, "pk", "id", "media_id", "shortcode") ?? Get(n, "code");
    static Observation? Instagram(JsonElement n, List<JsonElement> parents, HashSet<string?> children)
    {
        var code = Get(n, "code", "shortcode", "media_code", "pk_string"); var id = Id(n); if (code is null || id is null || children.Contains(id)) return null;
        var user = At(n, "user"); if (user.ValueKind != JsonValueKind.Object) user = At(n, "owner"); var handle = Get(user, "username") ?? Get(n, "username"); var uid = Get(user, "pk", "id") ?? Get(n, "user_id", "owner") ?? handle;
        if (uid is null && At(n, "image_versions2").ValueKind == JsonValueKind.Undefined && At(n, "display_resources").ValueKind == JsonValueKind.Undefined && At(n, "display_url").ValueKind == JsonValueKind.Undefined && At(n, "thumbnail_src").ValueKind == JsonValueKind.Undefined && At(n, "video_versions").ValueKind == JsonValueKind.Undefined && At(n, "carousel_media").ValueKind == JsonValueKind.Undefined && At(n, "media_type").ValueKind == JsonValueKind.Undefined) return null;
        var p = new Post { Platform = "instagram", PlatformPostId = id, LikeRef = id, PostedAt = Date(Get(n, "taken_at", "taken_at_timestamp", "timestamp", "published")), Text = Get(n, "caption", "text", "caption_text"), ObservedAuthorName = Get(user, "full_name") ?? handle, ObservedAuthorUrl = handle is null ? null : $"https://www.instagram.com/{handle}/" };
        var product = Get(n, "product_type"); p.IsReel = product is "clips" or "igtv" or "reel" || Get(n, "media_type") is "2" or "5";
        p.Permalink = Url(Get(n, "permalink", "share_url"), p.Platform) ?? $"https://www.instagram.com/{(p.IsReel ? "reel" : "p")}/{code}/"; p.IsReel |= p.Permalink.Contains("/reel/", StringComparison.Ordinal);
        p.IsSponsored = Truth(At(n, "is_paid_partnership")) || Truth(At(n, "paid_partener")) || Truth(At(n, "is_paid_sponsored")) || Truth(At(n, "ad_id_latest")) || Truth(At(n, "ad_id")) || product == "ad";
        static bool IgSuggestedNode(JsonElement x) => Truth(At(x, "suggested_for_you")) || Suggested(Get(x, "unit_title")) || Suggested(Get(x, "title")) || Suggested(Get(x, "feed_type")) || Suggested(Get(x, "type"));
        bool isSuggested = IgSuggestedNode(n);
        if (!isSuggested) for (int i = 0; i < parents.Count; i++) if (IgSuggestedNode(parents[i])) { isSuggested = true; break; }
        p.IsSuggested = isSuggested;
        var tags = Array(At(n, "coauthor_producers")).Concat(Array(At(n, "coauthors"))).Select(x => new Tag(Get(x, "full_name", "username") ?? "", Get(x, "username") is { } h ? $"https://www.instagram.com/{h}/" : null)).ToArray(); if (tags.Length > 0) p.TagsJson = JsonSerializer.Serialize(tags, InstanceValidation.Json);
        var slides = Array(At(n, "carousel_media")).ToArray(); if (slides.Length == 0) slides = [n]; var media = new List<MediaSource>();
        foreach (var slide in slides) if (Image(slide) is { } image) media.Add(new("image", image, SourceKey(image)));
        if (media.Count == 0 && Image(n) is { } fallback) media.Add(new("image", fallback, SourceKey(fallback)));
        foreach (var slide in slides) if (Array(At(slide, "video_versions")).Select(v => Get(v, "url")).FirstOrDefault(x => x is not null) is { } video) media.Add(new("video", video, SourceKey(video)));
        if (p.IsReel && media.All(x => x.Kind != "video")) media.Add(new("video", p.Permalink, "permalink:" + id));
        return new(p, uid, p.ObservedAuthorName, p.ObservedAuthorUrl, Limit(media, 10))
        { TimelineOwnerIds = Array(At(n, "coauthor_producers")).Concat(Array(At(n, "coauthors"))).Select(x => Get(x, "pk", "id")).OfType<string>().Distinct().ToArray() };
    }
    static bool Suggested(string? s) => s is not null && (s is "discover_media" or "discover_top_media" or "explore_grid_media" or "feed_backtracking_unit" or "feed_follow_requests" or "feed_suggestions_unit" or "media_with_liked_by" or "top_reels_media" or "suggested_for_you" or "suggested_for_you_unit" || s.Contains("suggested for you", StringComparison.OrdinalIgnoreCase) || s.Contains("for you page", StringComparison.OrdinalIgnoreCase));
    static string? Image(JsonElement n)
    {
        var candidates = Array(At(n, "image_versions2", "candidates")).ToArray(); if (candidates.Length == 0) candidates = Array(At(n, "display_resources")).ToArray();
        int Width(JsonElement c) => int.TryParse(Get(c, "width", "config_width"), out var w) ? w : 0;
        var choice = candidates.Where(x => Width(x) > 0 && Width(x) <= 1080).OrderByDescending(Width).FirstOrDefault();
        if (choice.ValueKind == JsonValueKind.Undefined) choice = candidates.Where(x => Width(x) > 0).OrderBy(Width).FirstOrDefault();
        if (choice.ValueKind == JsonValueKind.Undefined) choice = candidates.FirstOrDefault();
        return Get(choice, "url", "src") ?? Get(n, "thumbnail_src", "display_url");
    }
    static string? FacebookLikeRef(JsonElement story, string postId)
    {
        var direct = Get(At(story, "feedback"), "id", "targetID");
        if (!string.IsNullOrWhiteSpace(direct)) return direct;
        // Use only the identified post's UFI, never a shared story or comment.
        var owner = At(story, "comet_sections", "feedback", "story", "story_ufi_container", "story");
        if (Get(owner, "post_id") != postId) return null;
        return Get(At(owner, "feedback_context", "feedback_target_with_context",
            "comet_ufi_summary_and_actions_renderer", "feedback"), "id", "targetID");
    }
    static bool IsFacebookSponsoredNode(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in x.EnumerateObject())
        {
            if (prop.NameEquals("sponsored_data"u8) || prop.NameEquals("th_dat_spo"u8))
            {
                if (prop.Value.ValueKind == JsonValueKind.Object && HasAnyProperty(prop.Value)) return true;
            }
            else if (prop.NameEquals("ad_id"u8))
            {
                if (prop.Value.ValueKind != JsonValueKind.Null) return true;
            }
            else if (prop.NameEquals("__typename"u8))
            {
                if ((Text(prop.Value) ?? "").Contains("sponsored", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }
    static bool AnyFacebookSponsored(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in e.EnumerateObject())
            {
                var val = p.Value;
                if (p.NameEquals("sponsored_data"u8) || p.NameEquals("th_dat_spo"u8))
                {
                    if (val.ValueKind == JsonValueKind.Object && HasAnyProperty(val)) return true;
                }
                else if (p.NameEquals("ad_id"u8))
                {
                    if (val.ValueKind != JsonValueKind.Null) return true;
                }
                else if (p.NameEquals("__typename"u8))
                {
                    if ((Text(val) ?? "").Contains("sponsored", StringComparison.OrdinalIgnoreCase)) return true;
                }
                if (val.ValueKind is JsonValueKind.Object or JsonValueKind.Array && AnyFacebookSponsored(val)) return true;
            }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array && AnyFacebookSponsored(item)) return true;
        }
        return false;
    }
    static bool IsFacebookSuggestedNode(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in x.EnumerateObject())
        {
            if (prop.NameEquals("badge"u8))
            {
                if ((Get(prop.Value, "text") ?? "").Contains("suggested", StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (prop.NameEquals("tracking"u8))
            {
                var tracking = NonBlankText(prop.Value);
                if (tracking is not null && tracking.Contains("originated_from_recommendation", StringComparison.Ordinal) && RecommendationRegex().IsMatch(tracking)) return true;
            }
        }
        return false;
    }
    static JsonElement FindSharedStory(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            if (e.TryGetProperty("actors"u8, out var a) && a.ValueKind == JsonValueKind.Array
                && e.TryGetProperty("message"u8, out var m) && NonBlankText(m) is not null) return e;
            foreach (var p in e.EnumerateObject())
                if (p.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindSharedStory(p.Value);
                    if (found.ValueKind != JsonValueKind.Undefined) return found;
                }
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (item.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    var found = FindSharedStory(item);
                    if (found.ValueKind != JsonValueKind.Undefined) return found;
                }
        }
        return default;
    }
    static void ExtractTags(string text, JsonElement ranges, List<Tag> tags)
    {
        // Compute UTF-16 char offsets from Unicode scalar (rune) indices only when ranges exist.
        var runeStarts = new List<int>(text.Length + 1);
        int charPos = 0;
        foreach (var rune in text.EnumerateRunes()) { runeStarts.Add(charPos); charPos += rune.Utf16SequenceLength; }
        runeStarts.Add(charPos);
        int runeCount = runeStarts.Count - 1;
        var withStart = text.IndexOf(" is with ", StringComparison.Ordinal); var atEnd = text.LastIndexOf(" at ", StringComparison.Ordinal);
        foreach (var range in ranges.EnumerateArray())
        {
            var entity = At(range, "entity"); if (Get(entity, "__typename") != "User") continue;
            if (!int.TryParse(Get(range, "offset"), out var offset) || !int.TryParse(Get(range, "length"), out var length) || offset < 0 || length < 1 || offset + length > runeCount) continue;
            int utfOffset = runeStarts[offset];
            var name = text.Substring(utfOffset, runeStarts[offset + length] - utfOffset);
            tags.Add(new(name, Url(Get(entity, "url", "profile_url"), "facebook"), withStart >= 0 && utfOffset >= withStart + 9 && (atEnd < withStart || utfOffset < atEnd) ? "with" : null));
        }
    }
    static Observation? Facebook(JsonElement n, List<JsonElement> parents)
    {
        var substory = parents.Count >= 4 && Get(parents[^4], "__typename") == "CometStoryAggregatedStoriesStrategy"
            && At(parents[^4], "story").Equals(parents[^3]) && At(parents[^3], "interesting_substories").Equals(parents[^2]) && At(parents[^1], "node").Equals(n);
        if (!substory && !(n.TryGetProperty("__typename"u8, out var tnEl) && (Text(tnEl) ?? "").Contains("Story", StringComparison.Ordinal))) return null;
        var id = Get(n, "post_id", "id"); if (id is null || id == "0") return null;
        bool sponsored = false;
        for (int i = 0; i < parents.Count; i++) if (IsFacebookSponsoredNode(parents[i])) { sponsored = true; break; }
        var time = Date(Get(n, "creation_time"));
        if (time is null && substory) time = Array(At(n, "comet_sections", "context_layout", "story", "comet_sections", "metadata")).Select(m => Date(Get(At(m, "story"), "creation_time"))).FirstOrDefault(t => t is not null);
        // Check sponsored early only when creation_time is absent (so non-sponsored nested Story nodes bail out cheaply);
        // dated stories detect sponsored, suggested, media, tags and memory in one combined subtree pass below.
        if (time is null && !sponsored)
        {
            sponsored = AnyFacebookSponsored(n);
            if (!sponsored) return null;
        }
        var actor = Array(At(n, "actors")).FirstOrDefault(); var authorUrl = Url(Get(actor, "wwwURL", "url"), "facebook");
        var shared = At(n, "attached_story");
        if (!(At(shared, "actors").ValueKind == JsonValueKind.Array && Get(shared, "message") is not null))
        {
            shared = At(n, "comet_sections", "content", "story", "attached_story");
            if (!(At(shared, "actors").ValueKind == JsonValueKind.Array && Get(shared, "message") is not null))
                shared = FindSharedStory(At(n, "attachments"));
        }
        var caption = Get(n, "message") ?? Get(At(n, "comet_sections", "content", "story"), "message") ?? Get(n, "text");
        var p = new Post { Platform = "facebook", PlatformPostId = id, Text = caption, PostedAt = time, IsSponsored = sponsored, ObservedAuthorName = Get(actor, "name"), ObservedAuthorUrl = authorUrl, LikeRef = FacebookLikeRef(n, id), Permalink = Url(Get(n, "wwwURL", "permalink", "permalink_url", "url"), "facebook"), SharedAuthor = Get(Array(At(shared, "actors")).FirstOrDefault(), "name"), SharedText = Get(shared, "message"), SharedUrl = Url(Get(shared, "wwwURL", "permalink_url"), "facebook") };
        p.StoryTitle = Get(At(n, "comet_sections", "context_layout", "story", "comet_sections", "title", "story"), "title");
        // Life-event facts are Facebook's structured description, not the author's caption.
        var facts = Array(At(n, "attachments")).Select(a => At(a, "styles"))
            .Where(a => Get(a, "__typename") == "StoryAttachmentLifeEventStyleRenderer")
            .SelectMany(a => Array(At(a, "attachment", "properties")))
            .Select(a => Get(At(a, "value"), "text"))
            .Where(t => !string.IsNullOrWhiteSpace(t) && !Uri.TryCreate(t, UriKind.Absolute, out _));
        var description = string.Join("\n", new[] { p.StoryTitle }.Concat(facts).Where(t => !string.IsNullOrWhiteSpace(t)).Distinct());
        p.StoryTitle = description.Length == 0 ? null : description;
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
        p.IsReel = p.Permalink?.Contains("/reel/", StringComparison.Ordinal) == true || p.Permalink?.Contains("/reels/", StringComparison.Ordinal) == true; p.IsEvent = p.Permalink?.Contains("/events/", StringComparison.Ordinal) == true;
        bool isSuggested = false;
        for (int i = 0; i < parents.Count; i++) if (IsFacebookSuggestedNode(parents[i])) { isSuggested = true; break; }
        var media = new List<MediaSource>(); var widths = new Dictionary<string, int>(); bool videoHint = false;
        var tags = new List<Tag>();
        var childBuf = new List<(JsonElement Elem, bool Media)>(32);
        void AddImage(string url, string? width)
        {
            var source = new MediaSource("image", url, SourceKey(url)); int.TryParse(width, out var w);
            // Two renditions of one asset share a key (SourceKey). The wider one takes the first-seen slot: in 100 of the 156
            // live posts that listed two renditions (2026-11-10) the thumbnail came first, and the card shows the slot at full width.
            var index = media.FindIndex(m => m.Kind == "image" && m.SourceKey == source.SourceKey);
            if (index < 0) { media.Add(source); widths[source.SourceKey] = w; } else if (w > widths[source.SourceKey]) { media[index] = source; widths[source.SourceKey] = w; }
        }
        // Single pre-order traversal of the Story subtree with one EnumerateObject() per node using UTF-8 property comparisons
        // instead of four separate walks and ~19 TryGetProperty linear scans per object.
        void InspectStory(JsonElement x, bool collectMedia)
        {
            if (x.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in x.EnumerateArray())
                    if (a.ValueKind is JsonValueKind.Object or JsonValueKind.Array) InspectStory(a, collectMedia);
                return;
            }
            if (x.ValueKind != JsonValueKind.Object) return;

            JsonElement imgObj = default, uriEl = default, widthEl = default, videoDeliveryEl = default;
            JsonElement hdUrlEl = default, sdUrlEl = default, playableUrlEl = default;
            JsonElement rangesEl = default, textEl = default;
            bool hasWidthOrHeight = false, isThrowback = false;
            int childStart = childBuf.Count;

            foreach (var prop in x.EnumerateObject())
            {
                var val = prop.Value;
                if (!sponsored)
                {
                    if (prop.NameEquals("sponsored_data"u8) || prop.NameEquals("th_dat_spo"u8))
                    {
                        if (val.ValueKind == JsonValueKind.Object && HasAnyProperty(val)) sponsored = true;
                    }
                    else if (prop.NameEquals("ad_id"u8))
                    {
                        if (val.ValueKind != JsonValueKind.Null) sponsored = true;
                    }
                }
                if (prop.NameEquals("__typename"u8))
                {
                    var tn = Text(val);
                    if (tn is not null)
                    {
                        if (!sponsored && tn.Contains("sponsored", StringComparison.OrdinalIgnoreCase)) sponsored = true;
                        if (tn.Contains("Throwback", StringComparison.OrdinalIgnoreCase)) isThrowback = true;
                    }
                }
                else if (!isSuggested && prop.NameEquals("badge"u8))
                {
                    if ((Get(val, "text") ?? "").Contains("suggested", StringComparison.OrdinalIgnoreCase)) isSuggested = true;
                }
                else if (!isSuggested && prop.NameEquals("tracking"u8))
                {
                    var tracking = NonBlankText(val);
                    if (tracking is not null && tracking.Contains("originated_from_recommendation", StringComparison.Ordinal) && RecommendationRegex().IsMatch(tracking))
                        isSuggested = true;
                }
                else if (prop.NameEquals("ranges"u8))
                {
                    if (val.ValueKind == JsonValueKind.Array && val.GetArrayLength() > 0) rangesEl = val;
                }
                else if (prop.NameEquals("text"u8))
                {
                    textEl = val;
                }

                if (collectMedia)
                {
                    if (prop.NameEquals("image"u8)) imgObj = val;
                    else if (prop.NameEquals("uri"u8)) uriEl = val;
                    else if (prop.NameEquals("width"u8)) { hasWidthOrHeight = true; widthEl = val; }
                    else if (prop.NameEquals("height"u8)) hasWidthOrHeight = true;
                    else if (prop.NameEquals("videoDeliveryResponseFragment"u8)) videoDeliveryEl = val;
                    else if (prop.NameEquals("browser_native_hd_url"u8)) hdUrlEl = val;
                    else if (prop.NameEquals("browser_native_sd_url"u8)) sdUrlEl = val;
                    else if (prop.NameEquals("playable_url"u8)) playableUrlEl = val;
                    else if (prop.NameEquals("video"u8)) videoHint = true;
                }

                if (val.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                {
                    bool childMedia = collectMedia
                        && !prop.NameEquals("actors"u8) && !prop.NameEquals("feedback"u8) && !prop.NameEquals("badge"u8)
                        && !prop.NameEquals("profile_picture"u8) && !prop.NameEquals("profilePicture"u8) && !prop.NameEquals("interesting_substories"u8);
                    childBuf.Add((val, childMedia));
                }
            }

            if (isThrowback) { p.MemoryLabel = Get(x, "title"); p.MemoryText = Get(At(x, "target"), "message"); }
            if (rangesEl.ValueKind == JsonValueKind.Array && NonBlankText(textEl) is { } rangeText)
                ExtractTags(rangeText, rangesEl, tags);
            if (collectMedia)
            {
                var image = Get(imgObj, "uri") ?? (hasWidthOrHeight ? NonBlankText(uriEl) : null);
                if (image is not null && !image.Contains("/rsrc.php/", StringComparison.Ordinal))
                    AddImage(image, Get(imgObj, "width") ?? NonBlankText(widthEl));
                string? video = NonBlankText(hdUrlEl) ?? NonBlankText(sdUrlEl) ?? NonBlankText(playableUrlEl);
                if (video is null && videoDeliveryEl.ValueKind == JsonValueKind.Object)
                {
                    video = Array(At(videoDeliveryEl, "videoDeliveryResponseResult", "progressive_urls"))
                        .Where(v => At(v, "failure_reason").ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                        .OrderByDescending(v => Get(At(v, "metadata"), "quality") == "HD")
                        .Select(v => Url(Get(v, "progressive_url"), "facebook")).FirstOrDefault(v => v is not null);
                }
                if (video is not null) { media.Add(new("video", video, SourceKey(video))); videoHint = true; }
            }

            int childEnd = childBuf.Count;
            for (int i = childStart; i < childEnd; i++)
            {
                var child = childBuf[i];
                InspectStory(child.Elem, child.Media);
            }
            if (childEnd > childStart) childBuf.RemoveRange(childStart, childEnd - childStart);
        }
        InspectStory(n, true);
        p.IsSponsored = sponsored;
        p.IsSuggested = isSuggested;
        if (videoHint && media.All(x => x.Kind != "video") && p.Permalink is not null) media.Add(new("video", p.Permalink, "permalink:" + id));
        if (string.IsNullOrWhiteSpace(p.Text) && string.IsNullOrWhiteSpace(p.SharedText) && media.Count == 0
            && (string.IsNullOrWhiteSpace(p.StoryTitle) || Get(actor, "id") is null
                || At(n, "comet_sections", "content", "story", "comet_sections", "aggregated_stories").ValueKind == JsonValueKind.Object)) return null;
        if (tags.Count > 0) p.TagsJson = JsonSerializer.Serialize(tags.DistinctBy(t => (t.Name, t.Url)), InstanceValidation.Json);
        var aid = Get(actor, "id", "idString", "__dr") ?? Identity.UrlRef("facebook", authorUrl)?[3..];
        return new(p, aid, p.ObservedAuthorName, authorUrl, Limit(media, 8));
    }
    static MediaSource[] Limit(List<MediaSource> media, int limit) => media.Where(x => x.Kind == "image").DistinctBy(x => x.SourceKey).Take(limit).Concat(media.Where(x => x.Kind == "video").Take(1)).ToArray();
    static Person? PersonFrom(string platform, JsonElement n, List<JsonElement> parents)
    {
        if (platform == "instagram")
        {
            var handle = Get(n, "username");
            if (handle is null || Get(n, "code", "shortcode") is not null) return null;
            for (int i = 0; i < parents.Count; i++) if (Get(parents[i], "code", "shortcode") is not null) return null;
            return new(Get(n, "pk", "id") ?? handle, Get(n, "full_name") ?? handle, $"https://www.instagram.com/{handle}/", Get(n, "profile_pic_url_hd", "profile_pic_url"));
        }
        var hint = Get(n, "type") == "FRIEND"; var name = Get(n, "title"); var url = Url(Get(n, "url", "link_url"), platform); if (name is null || url is null || !(hint || At(n, "profile_picture").ValueKind == JsonValueKind.Object || (Get(n, "__typename") ?? "").Contains("User"))) return null;
        var reference = Identity.UrlRef(platform, url); if (reference is null) return null;
        var numeric = new[] { reference[3..], Get(n, "userID", "id", "idString", "__dr") }.FirstOrDefault(x => long.TryParse(x, out _));
        return new(numeric ?? Get(n, "ent_id") ?? reference[3..], name, url, Get(n, "img_url") ?? Get(At(n, "profile_picture"), "uri"));
    }
    public static IEnumerable<(string Body, string Operation)> EmbeddedCaptures(string html)
    {
        foreach (Match match in ScriptJsonRegex().Matches(html))
        {
            JsonElement root; try { using var doc = JsonDocument.Parse(match.Groups[1].Value); root = doc.RootElement.Clone(); } catch (JsonException) { continue; }
            foreach (var payload in EmbeddedWalk(root)) yield return payload;
        }
    }
    static IEnumerable<(string Body, string Operation)> EmbeddedWalk(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { var a = e.EnumerateArray().ToArray(); if (a.Length == 2 && Text(a[0])?.Contains("Feed", StringComparison.OrdinalIgnoreCase) == true && At(a[1], "__bbox", "result", "data").ValueKind == JsonValueKind.Object) yield return (At(a[1], "__bbox", "result").GetRawText(), Text(a[0])!); foreach (var child in a) foreach (var p in EmbeddedWalk(child)) yield return p; }
        if (e.ValueKind == JsonValueKind.Object) foreach (var property in e.EnumerateObject()) foreach (var p in EmbeddedWalk(property.Value)) yield return p;
    }
}
