using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Feed.Core.Domain;

public static class Identity
{
    static readonly HashSet<string> NonProfiles = new("groups photo photos reel reels watch events event p tv explore stories accounts direct about help privacy policies terms login checkpoint home.php notifications messages marketplace gaming pages settings friends me profile share sharer.php story.php permalink.php plugins dialog search hashtag hashtags recover reg business ads developers saved live videos video ig_sso_users legal challenge download directory web static rsrc.php".Split(' '));
    // The second path segment of a profile link that is still that profile: the stored friends raws carry
    // "/<slug>/friends_mutual" links, and operators paste "/<handle>/reels/". Any other two-segment path yields no key.
    static readonly HashSet<string> FacebookTabs = new("about photos videos reels friends friends_mutual friends_all posts tagged followers following saved map".Split(' '));
    static readonly HashSet<string> InstagramTabs = new("reels tagged saved followers following".Split(' '));
    public static string? UrlRef(string platform, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return null;
        var domains = platform == "facebook" ? new[] { "facebook.com", "fb.com" } : ["instagram.com"];
        if (!domains.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))) return null;
        var segments = Uri.UnescapeDataString(url.AbsolutePath).Split('/', StringSplitOptions.RemoveEmptyEntries).Select(x => x.ToLowerInvariant()).ToArray();
        if (segments.Length == 0) return null;
        string? key = null;
        if (platform == "facebook" && segments[0] == "profile.php") key = Regex.Match(url.Query, @"(?:\?|&)id=(\d+)").Groups[1].Value;
        // "/people/<name>/<id>": the id is the profile id. Until 2026-11 the first segment became the key "fb:people" for every such author.
        else if (platform == "facebook" && segments[0] == "people") key = segments.Length == 3 && IsPlatformId(segments[2]) ? segments[2] : null;
        // Allowlist, not blocklist: a shape the parser has not seen yields no key instead of a key shared by everyone with that shape.
        else if (segments.Length == 1 || segments.Length == 2 && (platform == "facebook" ? FacebookTabs : InstagramTabs).Contains(segments[1])) key = segments[0].TrimStart('@');
        // A ".php" segment is a page, never a profile (photo.php, video.php); profile.php was handled above.
        if (string.IsNullOrWhiteSpace(key) || key.EndsWith(".php") || NonProfiles.Contains(key) || !Regex.IsMatch(key, @"^[a-z0-9_.-]+$")) return null;
        return Platforms.Prefix(platform) + ":" + key;
    }
    public static string[] Refs(string platform, string? id, string? url)
    {
        var refs = new List<string>();
        if (UsableId(id)) refs.Add(Platforms.Prefix(platform) + ":" + id!.Trim().ToLowerInvariant().TrimStart('@'));
        if (UrlRef(platform, url) is { } reference) refs.Add(reference);
        return refs.Distinct().Order().ToArray();
    }
    static bool UsableId(string? id) => !string.IsNullOrWhiteSpace(id) && id != "0" && !new[] { "unknown", "null", "none" }.Contains(id.ToLowerInvariant());
    // A platform id is all digits. The parsers fall back to a slug or a handle when a payload carries no id
    // (PayloadParser PersonFrom and the Facebook actor fallback); such a value is an alias, never proof of one account.
    public static bool IsPlatformId(string? id) => !string.IsNullOrEmpty(id) && id.All(char.IsAsciiDigit);
    // The one ref that may join two rows: the key built from a numeric platform id. Every other ref in Refs is an alias.
    public static string? IdRef(string platform, string? id) => UsableId(id) && IsPlatformId(id!.Trim()) ? Platforms.Prefix(platform) + ":" + id.Trim() : null;
    public static string Normalize(string value)
    {
        var b = new StringBuilder(); foreach (char c in value.Normalize(NormalizationForm.FormD)) if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) b.Append(char.ToLowerInvariant(c));
        return Regex.Replace(b.ToString(), @"\s+", " ").Trim();
    }
    static string Stem(string t) => t.Length > 5 && new[] { "ovi", "ova", "ovy" }.Any(t.EndsWith) ? t[..^3] : t;
    public static bool NameMatches(string query, string? name)
    {
        var q = Normalize(query).Split(' ', StringSplitOptions.RemoveEmptyEntries); var n = Normalize(name ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return q.Length > 0 && q.All(a => n.Any(b => a.Length >= 3 && b.Length >= 3 && (Stem(a).StartsWith(Stem(b), StringComparison.Ordinal) || Stem(b).StartsWith(Stem(a), StringComparison.Ordinal))));
    }
    public static string[] Keys(Author a) => JsonSerializer.Deserialize<string[]>(a.RefsJson) ?? [];
    public static bool Matches(string entry, Author? a)
    {
        if (a is null) return false;
        var query = entry.Trim().ToLowerInvariant().TrimStart('@');
        if (query.Contains(':')) return Keys(a).Contains(query);
        return Keys(a).Any(k => k[(k.IndexOf(':') + 1)..] == query) || NameMatches(entry, a.DisplayName);
    }
    public static IEnumerable<Author> Resolve(string entry, IEnumerable<Author> authors)
    {
        var all = authors.ToArray(); var query = entry.ToLowerInvariant().Trim().TrimStart('@');
        var exact = all.Where(a => Keys(a).Any(k => k == query || (!query.Contains(':') && k[(k.IndexOf(':') + 1)..] == query))).ToArray();
        return exact.Length > 0 ? exact : query.Contains(':') ? [] : all.Where(a => NameMatches(entry, a.DisplayName));
    }
    public static string Safe(string s) { var value = Regex.Replace(s, "[^a-zA-Z0-9._-]", "_"); return value is "" or "." or ".." ? "unknown" : value; }
}
// The always-show and close-friend entries, resolved to author ids. One entry costs a pass over all authors, and
// Identity.Keys parses RefsJson for each of them. A bulk pass therefore builds this once and shares it between its
// RuleContexts: without sharing, a refilter of 10,800 posts took about 14 seconds (measured 2026-11-10).
public sealed class ResolvedRules(IReadOnlyList<Author> authors, InstanceSnapshot instance)
{
    readonly Lazy<HashSet<long>> shown = new(() => instance.Preferences.Shows.SelectMany(e => Identity.Resolve(e, authors)).Select(a => a.Id).ToHashSet());
    // A close-friend entry resolves only among the friends of its own platform.
    readonly Lazy<HashSet<long>> closeFriends = new(() => authors.Where(a => a.IsFriend).GroupBy(a => a.Platform).SelectMany(g => instance.Config.Platform(g.Key).CloseFriends.SelectMany(e => Identity.Resolve(e, g))).Select(a => a.Id).ToHashSet());
    public bool Shown(Author a) => shown.Value.Contains(a.Id);
    public bool CloseFriend(Author a) => closeFriends.Value.Contains(a.Id);
}
public sealed record RuleContext(Author? Author, IReadOnlyList<Author> Authors, InstanceSnapshot Instance, ResolvedRules? Shared = null)
{
    readonly ResolvedRules resolved = Shared ?? new(Authors, Instance);
    public bool Shield(string gate) => Author is not null && Instance.Config.Filters.AlwaysShowBypasses.Contains(gate) && resolved.Shown(Author);
    public bool CloseFriend => Author is { IsFriend: true } && resolved.CloseFriend(Author);
    public bool FriendTag(Tag t, string platform) => Identity.UrlRef(platform, t.Url) is { } r && Authors.Any(a => a.Platform == platform && a.IsFriend && Identity.Keys(a).Contains(r));
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
    public static (string Owner, string Reason)? Evaluate(Post p, RuleContext c)
    {
        var f = c.Instance.Config.Filters;
        if (!c.Shield("types")) foreach (var (type, flag) in new[] { ("sponsored", p.IsSponsored), ("suggested", p.IsSuggested), ("event", p.IsEvent), ("reel", p.IsReel) }) if (flag && f.BlockedTypes.Contains(type)) return ("structural", type);
        if (f.Audience == "friends_and_followed" && !c.Shield("audience") && c.Author?.IsFriend != true && !Tags(p).Any(t => f.FriendTagException != "none" && (f.FriendTagException == "any" || t.Kind == "with") && c.FriendTag(t, p.Platform))) return ("whitelist", "outside configured audience");
        if (!c.Shield("keyword")) foreach (var phrase in c.Instance.Preferences.Keywords) if ((p.Text ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase) || (p.SharedText ?? "").Contains(phrase, StringComparison.OrdinalIgnoreCase)) return ("keyword", "keyword: " + phrase);
        foreach (var entry in c.Instance.Preferences.Mutes) if (Identity.Matches(entry, c.Author) || c.Author is null && !entry.Contains(':') && Identity.NameMatches(entry, p.ObservedAuthorName)) return ("mute", "muted: " + entry);
        return null;
    }
    // Writes only on a change, so an unchanged post keeps its HiddenAt and VisibilityRevision.
    public static void Apply(Post p, RuleContext c)
    {
        if (Visibility.Decide(p, c) is { } d) { if (!p.Hidden || p.HiddenBy != d.Owner || p.HiddenReason != d.Reason) p.SetHidden(d.Owner, d.Reason); }
        else if (p.Hidden) p.ClearHidden();
    }
    public static string[] Restrict(IEnumerable<string> categories, RuleContext c, string platform)
    {
        var result = categories.Where(k => c.Instance.Taxonomy.Categories.Any(x => x.Key == k && (c.CloseFriend || !x.CloseFriendsOnly.Contains(platform)))).Distinct().Take(3).ToArray();
        return result.Length == 0 && c.Instance.Taxonomy.Categories.FirstOrDefault(x => x.Default && (c.CloseFriend || !x.CloseFriendsOnly.Contains(platform))) is { } fallback ? [fallback.Key] : result;
    }
}
