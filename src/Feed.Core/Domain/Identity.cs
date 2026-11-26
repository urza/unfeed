using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Feed.Core.Domain;

public static partial class Identity
{
    static readonly HashSet<string> NonProfiles = new("groups photo photos reel reels watch events event p tv explore stories accounts direct about help privacy policies terms login checkpoint home.php notifications messages marketplace gaming pages settings friends me profile share sharer.php story.php permalink.php plugins dialog search hashtag hashtags recover reg business ads developers saved live videos video ig_sso_users legal challenge download directory web static rsrc.php".Split(' '));
    // The second path segment of a profile link that is still that profile: the stored friends raws carry
    // "/<slug>/friends_mutual" links, and operators paste "/<handle>/reels/". Any other two-segment path yields no key.
    static readonly HashSet<string> FacebookTabs = new("about photos videos reels friends friends_mutual friends_all posts tagged followers following saved map".Split(' '));
    static readonly HashSet<string> InstagramTabs = new("reels tagged saved followers following".Split(' '));
    [GeneratedRegex(@"(?:\?|&)id=(\d+)")] private static partial Regex ProfileIdRegex();
    [GeneratedRegex("[^a-zA-Z0-9._-]")] private static partial Regex UnsafeCharRegex();
    static bool ValidKeyChars(string key)
    {
        foreach (char c in key)
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '.' or '-')) return false;
        return key.Length > 0;
    }
    static bool HostMatches(string host, string domain)
        => host.Equals(domain, StringComparison.OrdinalIgnoreCase)
            || host.Length > domain.Length && host[host.Length - domain.Length - 1] == '.' && host.EndsWith(domain, StringComparison.OrdinalIgnoreCase);
    public static string? UrlRef(string platform, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Contains("://", StringComparison.Ordinal) ? value : "https://" + value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return null;
        bool validHost = platform == "facebook"
            ? HostMatches(url.Host, "facebook.com") || HostMatches(url.Host, "fb.com")
            : HostMatches(url.Host, "instagram.com");
        if (!validHost) return null;
        var segments = Uri.UnescapeDataString(url.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) return null;
        for (int i = 0; i < segments.Length; i++) segments[i] = segments[i].ToLowerInvariant();
        string? key = null;
        if (platform == "facebook" && segments[0] == "profile.php") key = ProfileIdRegex().Match(url.Query).Groups[1].Value;
        // "/people/<name>/<id>": the id is the profile id. Until 2026-11 the first segment became the key "fb:people" for every such author.
        else if (platform == "facebook" && segments[0] == "people") key = segments.Length == 3 && IsPlatformId(segments[2]) ? segments[2] : null;
        // Allowlist, not blocklist: a shape the parser has not seen yields no key instead of a key shared by everyone with that shape.
        else if (segments.Length == 1 || segments.Length == 2 && (platform == "facebook" ? FacebookTabs : InstagramTabs).Contains(segments[1])) key = segments[0].TrimStart('@');
        // A ".php" segment is a page, never a profile (photo.php, video.php); profile.php was handled above.
        if (string.IsNullOrWhiteSpace(key) || key.EndsWith(".php", StringComparison.Ordinal) || NonProfiles.Contains(key) || !ValidKeyChars(key)) return null;
        return Platforms.Prefix(platform) + ":" + key;
    }
    public static string[] Refs(string platform, string? id, string? url)
    {
        var refs = new List<string>(2);
        if (UsableId(id)) refs.Add(Platforms.Prefix(platform) + ":" + id!.Trim().ToLowerInvariant().TrimStart('@'));
        if (UrlRef(platform, url) is { } reference) refs.Add(reference);
        if (refs.Count == 2)
        {
            if (refs[0] == refs[1]) refs.RemoveAt(1);
            else if (StringComparer.CurrentCulture.Compare(refs[0], refs[1]) > 0) (refs[0], refs[1]) = (refs[1], refs[0]);
        }
        return refs.ToArray();
    }
    static bool UsableId(string? id) => !string.IsNullOrWhiteSpace(id) && id != "0" && !id.Equals("unknown", StringComparison.OrdinalIgnoreCase) && !id.Equals("null", StringComparison.OrdinalIgnoreCase) && !id.Equals("none", StringComparison.OrdinalIgnoreCase);
    // A platform id is all digits. The parsers fall back to a slug or a handle when a payload carries no id
    // (PayloadParser PersonFrom and the Facebook actor fallback); such a value is an alias, never proof of one account.
    public static bool IsPlatformId(string? id)
    {
        if (string.IsNullOrEmpty(id)) return false;
        foreach (char c in id) if (!char.IsAsciiDigit(c)) return false;
        return true;
    }
    // The one ref that may join two rows: the key built from a numeric platform id. Every other ref in Refs is an alias.
    public static string? IdRef(string platform, string? id) => UsableId(id) && IsPlatformId(id!.Trim()) ? Platforms.Prefix(platform) + ":" + id.Trim() : null;
    public static string Normalize(string value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        bool ascii = true;
        foreach (char c in value) if (c >= 128) { ascii = false; break; }
        var source = ascii ? value : value.Normalize(NormalizationForm.FormD);
        var b = new StringBuilder(source.Length);
        bool space = false;
        foreach (char c in source)
        {
            if (!ascii && CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsWhiteSpace(c)) { if (b.Length > 0) space = true; continue; }
            if (space) { b.Append(' '); space = false; }
            b.Append(char.ToLowerInvariant(c));
        }
        return b.ToString();
    }
    internal static string Stem(string t) => t.Length > 5 && (t.EndsWith("ovi", StringComparison.Ordinal) || t.EndsWith("ova", StringComparison.Ordinal) || t.EndsWith("ovy", StringComparison.Ordinal)) ? t[..^3] : t;
    internal static string[] NormalizedStemmedTokens(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var parts = Normalize(value).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++) parts[i] = Stem(parts[i]);
        return parts;
    }
    internal static bool TokensMatch(string[] queryTokens, string[] nameTokens)
    {
        if (queryTokens.Length == 0 || nameTokens.Length == 0) return false;
        foreach (var a in queryTokens)
        {
            if (a.Length < 3) return false;
            bool found = false;
            foreach (var b in nameTokens)
                if (b.Length >= 3 && (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal))) { found = true; break; }
            if (!found) return false;
        }
        return true;
    }
    public static bool NameMatches(string query, string? name)
    {
        var q = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries); var n = Normalize(name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return q.Length > 0 && q.All(a => n.Any(b => a.Length >= 3 && b.Length >= 3 && (Stem(a).StartsWith(Stem(b), StringComparison.Ordinal) || Stem(b).StartsWith(Stem(a), StringComparison.Ordinal))));
    }
    public static string[] Keys(Author a) => string.IsNullOrEmpty(a.RefsJson) || a.RefsJson == "[]" ? [] : JsonSerializer.Deserialize<string[]>(a.RefsJson) ?? [];
    public static bool Matches(string entry, Author? a)
    {
        if (a is null) return false;
        var query = entry.Trim().ToLowerInvariant().TrimStart('@');
        if (query.Contains(':')) return Keys(a).Contains(query);
        return Keys(a).Any(k => k[(k.IndexOf(':') + 1)..] == query) || NameMatches(entry, a.DisplayName);
    }
    public static IEnumerable<Author> Resolve(string entry, IEnumerable<Author> authors)
    {
        var all = authors as Author[] ?? authors.ToArray(); var query = entry.ToLowerInvariant().Trim().TrimStart('@');
        bool hasColon = query.Contains(':');
        var exact = new List<Author>();
        foreach (var a in all)
        {
            foreach (var k in Keys(a))
                if (k == query || (!hasColon && k[(k.IndexOf(':') + 1)..] == query)) { exact.Add(a); break; }
        }
        if (exact.Count > 0) return exact;
        if (hasColon) return [];
        var q = Normalize(entry).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 0 || q.Any(t => t.Length < 3)) return [];
        for (int i = 0; i < q.Length; i++) q[i] = Stem(q[i]);
        var matched = new List<Author>();
        foreach (var a in all)
        {
            var n = NormalizedStemmedTokens(a.DisplayName);
            if (TokensMatch(q, n)) matched.Add(a);
        }
        return matched;
    }
    public static string Safe(string s) { var value = UnsafeCharRegex().Replace(s, "_"); return value is "" or "." or ".." ? "unknown" : value; }
}
// The always-show, close-friend, mute, friend-tag and category-restriction lookups, pre-indexed once over the
// author list and shared across RuleContexts. One entry previously scanned all authors and parsed RefsJson for
// each of them, and mutes/friend-tags repeated that work per post.
public sealed class ResolvedRules
{
    readonly IReadOnlyList<Author> authors;
    readonly InstanceSnapshot instance;
    readonly bool blockSponsored, blockSuggested, blockEvent, blockReel;
    readonly bool bypassTypes, bypassAudience, bypassKeyword, bypassLlm;
    readonly bool audienceRestricted;
    readonly bool allowTagAny, allowTagWith;
    readonly string[] keywords;
    readonly SearchValues<string>? keywordSearch;
    readonly (string Entry, string[] QueryTokens)[] authorlessMutes;
    readonly Dictionary<string, Category> categoriesByKey;
    readonly Dictionary<string, string?> defaultCategoryByPlatform;
    readonly Dictionary<string, string?> defaultCloseFriendCategoryByPlatform;
    Dictionary<(string CategoriesJson, bool CloseFriend, string Platform), string>? restrictJsonCache;

    // Lazy author indexes: built at most once per ResolvedRules instance, parsing each author's RefsJson once.
    Dictionary<string, List<Author>>? byExactKey;
    Dictionary<string, List<Author>>? byBareKey;
    string[][]? authorKeys;
    string[][]? authorNameTokens;
    HashSet<long>? shown;
    HashSet<long>? closeFriends;
    Dictionary<long, string>? mutedByAuthorId;
    HashSet<(string Platform, string Key)>? friendKeys;

    public ResolvedRules(IReadOnlyList<Author> authors, InstanceSnapshot instance)
    {
        this.authors = authors;
        this.instance = instance;
        var f = instance.Config.Filters;
        blockSponsored = f.BlockedTypes.Contains("sponsored");
        blockSuggested = f.BlockedTypes.Contains("suggested");
        blockEvent = f.BlockedTypes.Contains("event");
        blockReel = f.BlockedTypes.Contains("reel");
        bypassTypes = f.AlwaysShowBypasses.Contains("types");
        bypassAudience = f.AlwaysShowBypasses.Contains("audience");
        bypassKeyword = f.AlwaysShowBypasses.Contains("keyword");
        bypassLlm = f.AlwaysShowBypasses.Contains("llm");
        audienceRestricted = f.Audience == "friends_and_followed";
        allowTagAny = f.FriendTagException == "any";
        allowTagWith = f.FriendTagException == "with";
        keywords = instance.Preferences.Keywords.IsDefaultOrEmpty ? [] : [.. instance.Preferences.Keywords];
        keywordSearch = keywords.Length > 1 ? SearchValues.Create(keywords, StringComparison.OrdinalIgnoreCase) : null;
        var mutes = instance.Preferences.Mutes;
        if (mutes.IsDefaultOrEmpty) authorlessMutes = [];
        else
        {
            var list = new List<(string, string[])>();
            foreach (var entry in mutes)
            {
                if (entry.Contains(':')) continue;
                var q = Identity.Normalize(entry).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (q.Length > 0 && q.All(t => t.Length >= 3))
                {
                    for (int i = 0; i < q.Length; i++) q[i] = Identity.Stem(q[i]);
                    list.Add((entry, q));
                }
            }
            authorlessMutes = list.ToArray();
        }
        var cats = instance.Taxonomy.Categories;
        categoriesByKey = new Dictionary<string, Category>(cats.Length, StringComparer.Ordinal);
        foreach (var c in cats) categoriesByKey[c.Key] = c;
        defaultCategoryByPlatform = new Dictionary<string, string?>(StringComparer.Ordinal);
        defaultCloseFriendCategoryByPlatform = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var p in Platforms.All)
        {
            defaultCategoryByPlatform[p] = cats.FirstOrDefault(x => x.Default && !x.CloseFriendsOnly.Contains(p))?.Key;
            defaultCloseFriendCategoryByPlatform[p] = cats.FirstOrDefault(x => x.Default)?.Key;
        }
    }

    void EnsureKeyIndex()
    {
        if (byExactKey is not null) return;
        var exact = new Dictionary<string, List<Author>>(authors.Count * 2, StringComparer.Ordinal);
        var bare = new Dictionary<string, List<Author>>(authors.Count * 2, StringComparer.Ordinal);
        var keys = new string[authors.Count][];
        for (int i = 0; i < authors.Count; i++)
        {
            var a = authors[i];
            var ks = Identity.Keys(a);
            keys[i] = ks;
            foreach (var k in ks)
            {
                if (!exact.TryGetValue(k, out var el)) exact[k] = el = [];
                if (!el.Contains(a)) el.Add(a);
                var suffix = k[(k.IndexOf(':') + 1)..];
                if (!bare.TryGetValue(suffix, out var bl)) bare[suffix] = bl = [];
                if (!bl.Contains(a)) bl.Add(a);
            }
        }
        authorKeys = keys;
        byExactKey = exact;
        byBareKey = bare;
    }

    string[][] EnsureNameTokens()
    {
        if (authorNameTokens is not null) return authorNameTokens;
        var tokens = new string[authors.Count][];
        for (int i = 0; i < authors.Count; i++) tokens[i] = Identity.NormalizedStemmedTokens(authors[i].DisplayName);
        return authorNameTokens = tokens;
    }

    public IEnumerable<Author> Resolve(string entry, string? friendsPlatform = null)
    {
        EnsureKeyIndex();
        var query = entry.ToLowerInvariant().Trim().TrimStart('@');
        bool hasColon = query.Contains(':');
        List<Author>? exact = null;
        if (hasColon) byExactKey!.TryGetValue(query, out exact);
        else
        {
            byExactKey!.TryGetValue(query, out var e1);
            byBareKey!.TryGetValue(query, out var e2);
            if (e1 is null) exact = e2;
            else if (e2 is null) exact = e1;
            else { exact = [.. e1]; foreach (var a in e2) if (!exact.Contains(a)) exact.Add(a); }
        }
        if (exact is not null)
        {
            if (friendsPlatform is null) return exact;
            var filtered = exact.Where(a => a.IsFriend && a.Platform == friendsPlatform).ToList();
            if (filtered.Count > 0) return filtered;
        }
        if (hasColon) return [];
        var q = Identity.Normalize(entry).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (q.Length == 0 || q.Any(t => t.Length < 3)) return [];
        for (int i = 0; i < q.Length; i++) q[i] = Identity.Stem(q[i]);
        var tokens = EnsureNameTokens();
        var result = new List<Author>();
        for (int i = 0; i < authors.Count; i++)
        {
            var a = authors[i];
            if (friendsPlatform is not null && (!a.IsFriend || a.Platform != friendsPlatform)) continue;
            if (Identity.TokensMatch(q, tokens[i])) result.Add(a);
        }
        return result;
    }

    HashSet<long> ShownSet => shown ??= instance.Preferences.Shows.SelectMany(e => Resolve(e)).Select(a => a.Id).ToHashSet();
    HashSet<long> CloseFriendSet => closeFriends ??= BuildCloseFriends();
    HashSet<long> BuildCloseFriends()
    {
        var set = new HashSet<long>();
        foreach (var p in Platforms.All)
        {
            var entries = instance.Config.Platform(p).CloseFriends;
            if (entries.IsDefaultOrEmpty) continue;
            foreach (var e in entries) foreach (var a in Resolve(e, p)) set.Add(a.Id);
        }
        return set;
    }

    Dictionary<long, string> MutedMap => mutedByAuthorId ??= BuildMutedMap();
    Dictionary<long, string> BuildMutedMap()
    {
        var map = new Dictionary<long, string>();
        var mutes = instance.Preferences.Mutes;
        if (mutes.IsDefaultOrEmpty || authors.Count == 0) return map;
        EnsureKeyIndex();
        string[][]? tokens = null;
        foreach (var entry in mutes)
        {
            var query = entry.Trim().ToLowerInvariant().TrimStart('@');
            if (query.Contains(':'))
            {
                if (byExactKey!.TryGetValue(query, out var list))
                    foreach (var a in list) map.TryAdd(a.Id, entry);
            }
            else
            {
                if (byBareKey!.TryGetValue(query, out var list))
                    foreach (var a in list) map.TryAdd(a.Id, entry);
                var q = Identity.Normalize(entry).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (q.Length > 0 && q.All(t => t.Length >= 3))
                {
                    for (int i = 0; i < q.Length; i++) q[i] = Identity.Stem(q[i]);
                    tokens ??= EnsureNameTokens();
                    for (int i = 0; i < authors.Count; i++)
                        if (!map.ContainsKey(authors[i].Id) && Identity.TokensMatch(q, tokens[i])) map[authors[i].Id] = entry;
                }
            }
        }
        return map;
    }

    HashSet<(string Platform, string Key)> FriendKeySet => friendKeys ??= BuildFriendKeys();
    HashSet<(string Platform, string Key)> BuildFriendKeys()
    {
        EnsureKeyIndex();
        var set = new HashSet<(string, string)>();
        for (int i = 0; i < authors.Count; i++)
        {
            var a = authors[i];
            if (!a.IsFriend) continue;
            foreach (var k in authorKeys![i]) set.Add((a.Platform, k));
        }
        return set;
    }

    public bool Shown(Author a) => ShownSet.Contains(a.Id);
    public bool CloseFriend(Author a) => CloseFriendSet.Contains(a.Id);
    public bool Muted(Author a) => MutedMap.ContainsKey(a.Id);
    internal bool HasKeywords => keywords.Length > 0;
    internal bool HasBypass(string gate) => gate switch { "types" => bypassTypes, "audience" => bypassAudience, "keyword" => bypassKeyword, "llm" => bypassLlm, _ => instance.Config.Filters.AlwaysShowBypasses.Contains(gate) };
    internal bool FriendTag(Tag t, string platform) => Identity.UrlRef(platform, t.Url) is { } r && FriendKeySet.Contains((platform, r));
    internal (string Owner, string Reason)? Evaluate(Post p, Author? author)
    {
        bool isShown = author is not null && (bypassTypes || bypassAudience || bypassKeyword) && Shown(author);
        if (!(bypassTypes && isShown))
        {
            if (p.IsSponsored && blockSponsored) return ("structural", "sponsored");
            if (p.IsSuggested && blockSuggested) return ("structural", "suggested");
            if (p.IsEvent && blockEvent) return ("structural", "event");
            if (p.IsReel && blockReel) return ("structural", "reel");
        }
        if (audienceRestricted && !(bypassAudience && isShown) && author?.IsFriend != true)
        {
            bool tagPass = false;
            if ((allowTagAny || allowTagWith) && p.TagsJson is not null)
            {
                foreach (var t in Filters.Tags(p))
                    if ((allowTagAny || t.Kind == "with") && FriendTag(t, p.Platform)) { tagPass = true; break; }
            }
            if (!tagPass) return ("whitelist", "outside configured audience");
        }
        if (!(bypassKeyword && isShown) && keywords.Length > 0)
        {
            var text = p.Text; var shared = p.SharedText;
            // SearchValues scans all keywords in a single SIMD pass; only on a hit do we loop to preserve first-match order.
            bool anyMatch = keywordSearch is null
                || (text is not null && text.AsSpan().ContainsAny(keywordSearch))
                || (shared is not null && shared.AsSpan().ContainsAny(keywordSearch));
            if (anyMatch)
            {
                foreach (var phrase in keywords)
                    if (text is not null && text.Contains(phrase, StringComparison.OrdinalIgnoreCase) || shared is not null && shared.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                        return ("keyword", "keyword: " + phrase);
            }
        }
        if (author is not null)
        {
            if (MutedMap.TryGetValue(author.Id, out var mutedEntry)) return ("mute", "muted: " + mutedEntry);
        }
        else if (authorlessMutes.Length > 0 && !string.IsNullOrWhiteSpace(p.ObservedAuthorName))
        {
            var observedTokens = Identity.NormalizedStemmedTokens(p.ObservedAuthorName);
            foreach (var (entry, q) in authorlessMutes)
                if (Identity.TokensMatch(q, observedTokens)) return ("mute", "muted: " + entry);
        }
        return null;
    }
    internal string RestrictJson(string categoriesJson, bool closeFriend, string platform)
    {
        restrictJsonCache ??= new();
        var key = (categoriesJson, closeFriend, platform);
        if (restrictJsonCache.TryGetValue(key, out var cached)) return cached;
        var restricted = Restrict(JsonSerializer.Deserialize<string[]>(categoriesJson) ?? [], closeFriend, platform);
        var serialized = JsonSerializer.Serialize(restricted);
        var result = serialized == categoriesJson ? categoriesJson : serialized;
        restrictJsonCache[key] = result;
        return result;
    }
    internal string[] Restrict(IEnumerable<string> categories, bool closeFriend, string platform)
    {
        List<string>? kept = null;
        foreach (var k in categories)
        {
            if (!categoriesByKey.TryGetValue(k, out var cat)) continue;
            if (!closeFriend && cat.CloseFriendsOnly.Contains(platform)) continue;
            kept ??= new List<string>(3);
            if (!kept.Contains(k))
            {
                kept.Add(k);
                if (kept.Count == 3) break;
            }
        }
        if (kept is { Count: > 0 }) return kept.ToArray();
        var fallback = closeFriend
            ? defaultCloseFriendCategoryByPlatform.GetValueOrDefault(platform) ?? instance.Taxonomy.Categories.FirstOrDefault(x => x.Default)?.Key
            : defaultCategoryByPlatform.GetValueOrDefault(platform) ?? instance.Taxonomy.Categories.FirstOrDefault(x => x.Default && !x.CloseFriendsOnly.Contains(platform))?.Key;
        return fallback is not null ? [fallback] : [];
    }
}
public sealed record RuleContext(Author? Author, IReadOnlyList<Author> Authors, InstanceSnapshot Instance, ResolvedRules? Shared = null)
{
    internal readonly ResolvedRules Resolved = Shared ?? new(Authors, Instance);
    public bool Shield(string gate) => Author is not null && Resolved.HasBypass(gate) && Resolved.Shown(Author);
    public bool CloseFriend => Author is { IsFriend: true } && Resolved.CloseFriend(Author);
    public bool FriendTag(Tag t, string platform) => Resolved.FriendTag(t, platform);
}
public static class Visibility
{
    // The one visibility rule. Ingest, identity merge and both refilters all ask here, and verdict apply uses its
    // Model step, so two paths cannot give the same post different results. Returns null for a visible post.
    public static (string Owner, string Reason)? Decide(Post p, RuleContext c)
    {
        // Reserved owners are not derived from rules. Returning the stored pair makes Filters.Apply a no-op for them.
        if (p.Hidden && p.HiddenBy is "thumbs" or "other") return (p.HiddenBy, p.HiddenReason!);
        return Filters.Evaluate(p, c) ?? Model(p, c);
    }
    // The model's part of the rule, without the deterministic gates. Verdict apply uses it alone (see Processing).
    // Fail-open (binding rule 12): only a verdict for the current content hides. Judged is false after a content change.
    // An owner unhide of this same content outranks the model, but not the deterministic gates.
    public static (string Owner, string Reason)? Model(Post p, RuleContext c) => p.Judged && p.LlmScore < c.Instance.Config.Llm.Threshold && p.OwnerUnhiddenRevision != p.ContentRevision && !c.Shield("llm") ? ("llm", $"llm {p.LlmScore}: {p.LlmReason}") : null;
}
public static class Filters
{
    public static Tag[] Tags(Post p) => p.TagsJson is null ? [] : JsonSerializer.Deserialize<Tag[]>(p.TagsJson, InstanceValidation.Json) ?? [];
    public static (string Owner, string Reason)? Evaluate(Post p, RuleContext c) => c.Resolved.Evaluate(p, c.Author);
    // Writes only on a change, so an unchanged post keeps its HiddenAt and VisibilityRevision.
    public static void Apply(Post p, RuleContext c)
    {
        if (Visibility.Decide(p, c) is { } d) { if (!p.Hidden || p.HiddenBy != d.Owner || p.HiddenReason != d.Reason) p.SetHidden(d.Owner, d.Reason); }
        else if (p.Hidden) p.ClearHidden();
    }
    public static string[] Restrict(IEnumerable<string> categories, RuleContext c, string platform)
        => c.Resolved.Restrict(categories, c.CloseFriend, platform);
}
