using System.Text.RegularExpressions;
using Feed.Core.Domain;
using Microsoft.Playwright;
namespace Feed.Cli;
public static class Site
{
    public static string Selectors(string platform, bool timeline) => platform == "facebook" ? "div[role='feed'], section[role='feed'], [aria-label='News feed'], [role='main'] [role='article'], [role='main'] h4"
        + (timeline ? ", [role='main'] a[href*='/posts/'], [role='main'] a[href*='/photo/?fbid='], [role='main'] a[href*='/photo.php?fbid=']" : "")
        : timeline ? "article,a[href*='/p/'],a[href*='/reel/'],a[href*='/tv/']" : "article,main article,[role='feed'],[data-testid='feed']";
    public static string Timeline(string platform, string handle) => Uri.TryCreate(handle, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" ? handle : Platforms.Home(platform) + handle.Trim('/', '@') + "/";
    public static bool Gate(string platform, string url, string method, int status, string contentType, string? body, bool people)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        if (platform == "facebook")
        {
            if (!(uri.Host == "facebook.com" || uri.Host.EndsWith(".facebook.com")) || method != "POST" || !uri.AbsolutePath.Contains("/api/graphql/")) return false;
            if (people) return true;
            var query = uri.Query.TrimStart('?').Split('&').Concat((body ?? "").Split('&')); var friendly = query.FirstOrDefault(s => s.StartsWith("fb_api_req_friendly_name="));
            return friendly is not null && Uri.UnescapeDataString(friendly).Contains("Feed", StringComparison.Ordinal);
        }
        if (status != 200 || method is not ("GET" or "POST") || !(uri.Host == "instagram.com" || uri.Host.EndsWith(".instagram.com")) || !(contentType.Contains("json") || contentType.Contains("text/plain") || contentType.Contains("text/javascript"))) return false;
        var path = uri.AbsolutePath; if (people) return method == "GET" && path.Contains("/api/v1/friendships/") && path.Contains("/following/");
        if (path.Contains("graphql"))
        {
            var named = uri.Query.TrimStart('?').Split('&').Concat((body ?? "").Split('&')).FirstOrDefault(s => s.StartsWith("fb_api_req_friendly_name="));
            var operation = named is null ? null : System.Net.WebUtility.UrlDecode(named[(named.IndexOf('=') + 1)..]);
            // A profile page also fetches inbox, credentials, stories and promotions.
            // Named GraphQL capture is limited to feed and profile-post operations.
            if (operation is not null && !Regex.IsMatch(operation, @"^Polaris(?:Feed|ProfilePosts|ProfileReels)")) return false;
            if (operation is null && contentType.Contains("text/javascript")) return false;
        }
        return new[] { "/api/graphql", "/api/v1/feed", "/api/v1/user", "/api/v1/users", "/api/v1/friendships", "/api/v1/media", "/api/v1/discover", "/api/v1/explore", "/api/v1/clips", "/graphql/query" }.Any(path.Contains) && !new[] { "/ads/", "/comment", "/delete", "/direct", "/friendships/create", "/friendships/destroy", "/friendships/like", "/friendships/show_many", "/ig_sso_users", "/like/", "/logging", "/media/like", "/reels_tray", "/stories/tray", "/unlike/" }.Any(path.Contains);
    }
    public static async Task<bool> Checkpoint(IPage page, string platform)
    {
        var urls = platform == "facebook" ? new[] { "/checkpoint", "/login", "login/identify", "two_factor" } : ["/accounts/two_factor_authentication", "/challenge", "/accounts/disabled", "/accounts/login", "/email-confirmation", "/two_factor"];
        if (urls.Any(s => page.Url.Contains(s, StringComparison.OrdinalIgnoreCase))) return true;
        var text = (await page.ContentAsync()).ToLowerInvariant(); return (platform == "facebook" ? new[] { "unusual activity", "confirm your login", "confirm this was you", "two-factor" } : ["check your phone", "we sent you a code", "help us confirm", "account has been temporarily disabled", "this was me"]).Any(text.Contains);
    }
    public static async Task DismissNotificationPrompt(IPage page)
    {
        var title = new Regex(@"^(turn on (?:push )?notifications|enable (?:push )?notifications|zapnout (?:push )?(?:oznámení|upozornění)|povolit (?:push )?(?:oznámení|upozornění))\s*[?!.]?$", RegexOptions.IgnoreCase);
        var dismiss = new Regex(@"^(not now|no thanks|later|teď ne|nyní ne|ne teď|později)$", RegexOptions.IgnoreCase);
        var dialogs = page.GetByRole(AriaRole.Dialog);
        for (int i = 0; i < await dialogs.CountAsync(); i++)
        {
            var dialog = dialogs.Nth(i); if (!await dialog.IsVisibleAsync()) continue;
            var firstLine = (await dialog.InnerTextAsync()).Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
            if (!title.IsMatch(firstLine) && await dialog.GetByRole(AriaRole.Heading, new() { NameRegex = title }).CountAsync() == 0) continue;
            var buttons = dialog.GetByRole(AriaRole.Button, new() { NameRegex = dismiss });
            var visible = new List<ILocator>();
            for (int j = 0; j < await buttons.CountAsync(); j++) if (await buttons.Nth(j).IsVisibleAsync()) visible.Add(buttons.Nth(j));
            if (visible.Count != 1) throw new IOException("notification prompt blocks browsing; no unambiguous dismissal button; inspect noVNC");
            await visible[0].ClickAsync(new() { Timeout = 5000 });
            await dialog.WaitForAsync(new() { State = WaitForSelectorState.Hidden, Timeout = 5000 });
            Console.WriteLine("browser: dismissed notification prompt");
            // The DOM changed; recheck on the next poll rather than skipping another dialog.
            return;
        }
    }
    public static async Task<ILocator> OpenInstagramFollowing(IPage page)
    {
        // Current profile markup uses href="#"; navigating to /following/ only opens the grid.
        var following = page.GetByRole(AriaRole.Link, new() { NameRegex = new Regex(@"^[\d,.]+\s+following$", RegexOptions.IgnoreCase) });
        await following.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 20000 });
        await DismissNotificationPrompt(page);
        await following.ClickAsync(new() { Timeout = 10000 });
        var dialog = page.GetByRole(AriaRole.Dialog);
        await dialog.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 12000 });
        return dialog;
    }
    public static bool TimelineAuthorMatches(string platform, string url, Author? author, Observation observation)
    {
        var keys = Identity.Refs(platform, observation.AuthorKey, observation.AuthorUrl);
        if (author?.PlatformAuthorId is { } owner && observation.TimelineOwnerIds.Contains(owner)) return true;
        return author is not null ? keys.Intersect(Identity.Keys(author)).Any() : Identity.UrlRef(platform, url) is { } reference && keys.Contains(reference);
    }
    public static async Task<ILocator?> Heart(IPage page, string platform, bool done)
    {
        var names = platform == "facebook" ? done ? "^(remove love)$" : "^(like|like post|remove like|remove love|remove care|remove haha|remove wow|remove sad|remove angry)$" : done ? "^(unlike|remove like|unlike post)$" : "^(like|like post)$";
        var matches = page.GetByRole(AriaRole.Button, new() { NameRegex = new Regex(names, RegexOptions.IgnoreCase) });
        var candidates = new List<ILocator>();
        for (int i = 0; i < await matches.CountAsync(); i++) { var button = matches.Nth(i); if (await button.IsVisibleAsync() && (platform == "facebook" || await button.Locator("svg[width='24']").CountAsync() > 0)) candidates.Add(button); }
        if (candidates.Count > 1) throw new IOException("ambiguous post reaction controls; no click authorized");
        return candidates.SingleOrDefault();
    }
}
