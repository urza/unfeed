using Feed.Cli;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;

public sealed class FacebookHeartTests
{
    const string Target = "https://www.facebook.com/synthetic/posts/101";

    [Fact, Trait("Category", "Browser")]
    public async Task PermalinkDialogExcludesBackgroundAndCommentsAndConfirmsOnlyItsPost()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.facebook.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync(Target);
        await page.SetContentAsync("""
            <button aria-label="Remove Love" id="background">Background feed</button>
            <div role="dialog"><div role="dialog">
              <a href="/synthetic/posts/101?tracking=fixture#time">Synthetic timestamp</a>
              <div role="article" aria-label="Comment by Synthetic Person">
                <a href="/synthetic/posts/999?comment_id=1">Comment timestamp</a>
                <button aria-label="Remove Love" id="comment"><svg width="16"></svg></button>
              </div>
              <button aria-label="Like" id="small"><svg width="16"></svg></button>
              <button aria-label="Remove Like" id="post" onclick="this.setAttribute('aria-label','Remove Love')">Post reaction</button>
            </div></div>
            """);
        Assert.Null(await Site.Heart(page, "facebook", true, Target));
        var button = await Site.Heart(page, "facebook", false, Target);
        Assert.Equal("post", await button!.GetAttributeAsync("id"));
        await button.ClickAsync(); // Synthetic fixture only; verifies the same scoped done-state lookup.
        Assert.Equal("post", await (await Site.Heart(page, "facebook", true, Target))!.GetAttributeAsync("id"));
        Assert.Equal("Remove Love", await page.Locator("#comment").GetAttributeAsync("aria-label"));
        Assert.Equal("Remove Love", await page.Locator("#background").GetAttributeAsync("aria-label"));
    }

    [Fact, Trait("Category", "Browser")]
    public async Task MissingOrForeignDialogEvidenceNeverFallsBackToAUniqueBackgroundControl()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.facebook.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync(Target);
        foreach (var markup in new[] {
            "<button aria-label='Like'>Background while dialog loads</button>",
            "<div role='dialog'><a href='/synthetic/posts/999'>Other post</a><button aria-label='Like'>Other post</button></div>",
            "<div role='dialog'><article role='article' aria-label='Comment by Fixture'><a href='/synthetic/posts/101'>Link in comment</a></article><button aria-label='Like'>Wrong post</button></div>",
            "<div role='dialog'><a href='https://facebook.com.evil.test/synthetic/posts/101'>Wrong host</a><button aria-label='Like'>Wrong post</button></div>",
            "<div role='dialog'><button aria-label='Like'>Outer dialog</button><div role='dialog'><a href='/synthetic/posts/101'>Nested dialog link</a></div></div>"
        })
        {
            await page.SetContentAsync(markup);
            Assert.Null(await Site.Heart(page, "facebook", false, Target));
            Assert.Null(await Site.Heart(page, "facebook", true, Target));
        }
        await page.SetContentAsync("<div role='dialog'><a href='/synthetic/posts/101'>Target</a><button aria-label='Like'>Target</button></div>");
        Assert.Null(await Site.Heart(page, "facebook", false, "https://www.facebook.com/synthetic/posts/999"));
        Assert.Null(await Site.Heart(page, "facebook", false));
    }

    [Fact, Trait("Category", "Browser")]
    public async Task MultipleScopedControlsRemainAmbiguousIncludingMixedReactionStates()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.facebook.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync("https://www.facebook.com/permalink.php?story_fbid=101&id=501");
        await page.SetContentAsync("<div role='dialog'><a href='/synthetic/posts/101'>Target</a><button aria-label='Like'>One</button><button aria-label='Remove Love'>Two</button></div>");
        await Assert.ThrowsAsync<IOException>(() => Site.Heart(page, "facebook", false, Target));
        await Assert.ThrowsAsync<IOException>(() => Site.Heart(page, "facebook", true, Target));
        await page.Locator("button").Last.EvaluateAsync("e=>e.remove()");
        Assert.NotNull(await Site.Heart(page, "facebook", false, Target));
    }
}
