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
    public async Task ApprovedControlStaysBoundWhenTheDialogGrowsBeforeTheHover()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.facebook.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync(Target);
        await page.SetContentAsync("""
            <div role="dialog" id="dialog">
              <a href="/synthetic/posts/101">Synthetic timestamp</a>
              <button aria-label="Like" id="post" onclick="this.setAttribute('aria-label','Remove Love')">Post reaction</button>
            </div>
            """);
        var button = await Site.Heart(page, "facebook", false, Target);
        Assert.Equal("post", await button!.GetAttributeAsync("id"));
        // More comments render before the post control while the sender prepares the hover. The approved node must stay the target.
        await page.EvaluateAsync("""
            () => { const c = document.createElement('div'); c.setAttribute('role', 'article'); c.setAttribute('aria-label', 'Comment by Late Person');
                    c.innerHTML = '<button aria-label="Like" id="late" onclick="this.setAttribute(\'aria-label\',\'Remove Love\')"><svg width="16"></svg></button>';
                    document.getElementById('dialog').prepend(c); }
            """);
        await button.HoverAsync(new() { Timeout = 2000 }); await button.ClickAsync(new() { Timeout = 2000 });
        Assert.Equal("Remove Love", await page.Locator("#post").GetAttributeAsync("aria-label"));
        Assert.Equal("Like", await page.Locator("#late").GetAttributeAsync("aria-label"));
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
    public async Task RenamedPostControlIsFoundAndThePickerOpenerIsUsedWhenHoverDoesNotShowLove()
    {
        // Shape taken from the 2026-10-31 live DOM: the post control is "React with Like to <Author>'s post",
        // a sibling "React to <Author>'s post" (16 px icon) opens the picker, comments keep the short "Like".
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.RouteAsync("https://www.facebook.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync(Target);
        await page.SetContentAsync("""
            <button aria-label="React with Like to Other Person's post" id="background">Background feed</button>
            <div role="dialog"><div role="dialog" id="dialog">
              <a href="/synthetic/posts/101?__cft__[0]=x">Synthetic timestamp</a>
              <div role="article" aria-label="Comment by Synthetic Person">
                <button aria-label="Like" id="comment"><svg width="16"></svg></button>
              </div>
              <button aria-label="React with Like to Synthetic Author's post" id="post">40</button>
              <button aria-label="React to Synthetic Author's post" id="opener" onclick="document.getElementById('picker').hidden=false"><svg width="16"></svg></button>
              <div id="picker" hidden><button aria-label="Love" id="love" onclick="document.getElementById('post').setAttribute('aria-label','Remove Love from Synthetic Author\'s post')">Love</button></div>
              <button aria-label="React with Like to Synthetic Author's post" id="hidden-copy" style="display:none">40</button>
            </div></div>
            """);
        Assert.Null(await Site.Heart(page, "facebook", true, Target));
        var control = await Site.Heart(page, "facebook", false, Target);
        Assert.Equal("post", await control!.GetAttributeAsync("id"));
        var love = await Site.FacebookLoveOption(page, control);
        Assert.Equal("love", await love.GetAttributeAsync("id"));
        await love.ClickAsync(); // Synthetic fixture only; verifies the renamed done state.
        Assert.Equal("post", await (await Site.Heart(page, "facebook", true, Target))!.GetAttributeAsync("id"));
        Assert.Equal("Like", await page.Locator("#comment").GetAttributeAsync("aria-label"));
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
