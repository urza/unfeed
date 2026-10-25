using Feed.Cli;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;

public sealed class CheckpointTests
{
    static async Task<IPage> Page(IBrowser browser, string body, string url = "https://www.instagram.com/")
    {
        var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.instagram.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = body }));
        await page.GotoAsync(url);
        return page;
    }

    [Fact, Trait("Category", "Browser")]
    public async Task UrlAndTextMarkersAreRecognizedAndOrdinaryPagesAreNot()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var home = await Page(browser, "<main>Welcome back</main>");
        Assert.False(await Site.Checkpoint(home, "instagram"));
        var challenge = await Page(browser, "<main>Anything</main>", "https://www.instagram.com/challenge/synthetic/");
        Assert.True(await Site.Checkpoint(challenge, "instagram"));
        var text = await Page(browser, "<main>We sent you a code to your email.</main>");
        Assert.True(await Site.Checkpoint(text, "instagram"));
    }

    [Fact, Trait("Category", "Browser")]
    public async Task ProbeDuringAPendingNavigationDoesNotThrow()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await Page(browser, "<main>Logged out</main>");
        // The owner's login redirects the page while the login loop probes it. The slow route keeps a navigation
        // pending for a while; the probe must answer false or wait for the new document, never fail the login.
        await page.RouteAsync("https://www.instagram.com/slow", async r => { await Task.Delay(1500); await r.FulfillAsync(new() { ContentType = "text/html", Body = "<main>Home</main>" }); });
        var navigation = page.GotoAsync("https://www.instagram.com/slow");
        for (int i = 0; i < 20 && !navigation.IsCompleted; i++)
        {
            Assert.False(await Site.Checkpoint(page, "instagram"));
            await Task.Delay(100);
        }
        await navigation;
        Assert.False(await Site.Checkpoint(page, "instagram"));
    }
}
