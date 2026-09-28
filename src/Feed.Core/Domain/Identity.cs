using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace Feed.Core.Domain;

public static class Identity
{
    static readonly HashSet<string> NonProfiles = new("groups photo photos reel reels watch events event p tv explore stories accounts direct about help privacy policies terms login checkpoint home.php notifications messages marketplace gaming pages settings friends me profile share sharer.php story.php permalink.php plugins dialog search hashtag hashtags recover reg business ads developers saved live videos video ig_sso_users legal challenge download directory web static rsrc.php".Split(' '));
    public static string? UrlRef(string platform, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value.Contains("://") ? value : "https://" + value, UriKind.Absolute, out var url) || url.Scheme is not ("https" or "http")) return null;
        var domains = platform == "facebook" ? new[] { "facebook.com", "fb.com" } : ["instagram.com"];
        if (!domains.Any(h => url.Host.Equals(h, StringComparison.OrdinalIgnoreCase) || url.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase))) return null;
        string key = Uri.UnescapeDataString(url.AbsolutePath).Trim('/').Split('/')[0].ToLowerInvariant().TrimStart('@');
        if (platform == "facebook" && key == "profile.php") key = Regex.Match(url.Query, @"(?:\?|&)id=(\d+)").Groups[1].Value;
        if (string.IsNullOrWhiteSpace(key) || NonProfiles.Contains(key) || !Regex.IsMatch(key, @"^[a-z0-9_.-]+$")) return null;
        return Platforms.Prefix(platform) + ":" + key;
    }
    public static string[] Refs(string platform, string? id, string? url)
    {
        var refs = new List<string>();
        if (!string.IsNullOrWhiteSpace(id) && id != "0" && !new[] { "unknown", "null", "none" }.Contains(id.ToLowerInvariant())) refs.Add(Platforms.Prefix(platform) + ":" + id.Trim().ToLowerInvariant().TrimStart('@'));
        if (UrlRef(platform, url) is { } reference) refs.Add(reference);
        return refs.Distinct().Order().ToArray();
    }
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
public sealed record RuleContext(Author? Author, IReadOnlyList<Author> Authors, InstanceSnapshot Instance)
{
    public bool Shield(string gate) => Author is not null && Instance.Config.Filters.AlwaysShowBypasses.Contains(gate) && Instance.Preferences.Shows.Any(e => Identity.Resolve(e, Authors).Any(a => a.Id == Author.Id));
    public bool CloseFriend => Author is { IsFriend: true } && Instance.Config.Platform(Author.Platform).CloseFriends.Any(e => Identity.Resolve(e, Authors.Where(a => a.Platform == Author.Platform && a.IsFriend)).Any(a => a.Id == Author.Id));
    public bool FriendTag(Tag t, string platform) => Identity.UrlRef(platform, t.Url) is { } r && Authors.Any(a => a.Platform == platform && a.IsFriend && Identity.Keys(a).Contains(r));
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
    public static void Apply(Post p, RuleContext c)
    {
        if (p.HiddenBy is "thumbs" or "other") return;
        if (Evaluate(p, c) is { } d) { if (!p.Hidden || p.HiddenBy != d.Owner || p.HiddenReason != d.Reason) p.SetHidden(d.Owner, d.Reason); }
        else if (p.Hidden && (p.HiddenBy != "llm" || c.Shield("llm"))) p.ClearHidden();
    }
    public static string[] Restrict(IEnumerable<string> categories, RuleContext c, string platform)
    {
        var result = categories.Where(k => c.Instance.Taxonomy.Categories.Any(x => x.Key == k && (c.CloseFriend || !x.CloseFriendsOnly.Contains(platform)))).Distinct().Take(3).ToArray();
        return result.Length == 0 && c.Instance.Taxonomy.Categories.FirstOrDefault(x => x.Default && (c.CloseFriend || !x.CloseFriendsOnly.Contains(platform))) is { } fallback ? [fallback.Key] : result;
    }
}
