using Feed.Cli;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;

public sealed class InstagramHeartTests
{
    const string Target = "https://www.instagram.com/reel/synthetic_A-/";
    static async Task<IPage> Page(IBrowser browser)
    {
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1000, Height = 800 } });
        await page.RouteAsync("https://www.instagram.com/**", r => r.FulfillAsync(new() { ContentType = "text/html", Body = "<main></main>" }));
        await page.GotoAsync("https://www.instagram.com/reels/synthetic_A-/");
        return page;
    }

    [Fact, Trait("Category", "Browser")]
    public async Task ReelExcludesPrefetchedHeartsIncludingAlreadyLikedReelsAndNeverScrollsToThem()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await Page(browser);
        await page.SetContentAsync("""
            <main style="height:3000px">
              <button id="comment" aria-label="Like" style="position:absolute;top:40px"><svg width="16" height="16"></svg></button>
              <button id="post" aria-label="Like" style="position:absolute;top:100px" onclick="this.setAttribute('aria-label','Unlike')"><svg width="24" height="24"></svg></button>
              <button id="next" aria-label="Unlike" style="position:absolute;top:1000px"><svg width="24" height="24"></svg></button>
              <button id="later" aria-label="Like" style="position:absolute;top:2000px"><svg width="24" height="24"></svg></button>
            </main>
            """);
        Assert.True(await page.Locator("#next").IsVisibleAsync()); // DOM-visible is not viewport-visible.
        Assert.Null(await Site.Heart(page, "instagram", true, Target));
        var selected = await Site.Heart(page, "instagram", false, Target);
        Assert.Equal("post", await selected!.GetAttributeAsync("id"));
        await selected.ClickAsync();
        Assert.Equal("post", await (await Site.Heart(page, "instagram", true, Target))!.GetAttributeAsync("id"));
        Assert.Null(await Site.Heart(page, "instagram", false, Target));
        Assert.Equal(0, await page.EvaluateAsync<int>("scrollY"));
        Assert.Equal("Unlike", await page.Locator("#next").GetAttributeAsync("aria-label"));
        Assert.Equal("Like", await page.Locator("#comment").GetAttributeAsync("aria-label"));
        Assert.Null(await Site.Heart(page, "instagram", true, "https://www.instagram.com/reel/other/"));
        Assert.Null(await Site.Heart(page, "instagram", true, "https://instagram.com.evil.test/reel/synthetic_A-/"));
        Assert.Null(await Site.Heart(page, "instagram", true));
    }

    [Fact, Trait("Category", "Browser")]
    public async Task ObscuredAndClippedControlsCannotSupplyAReactionOrDoneState()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await Page(browser);
        await page.SetContentAsync("""
            <button aria-label="Unlike" style="position:absolute;left:30px;top:30px"><svg width="24" height="24"></svg></button>
            <div style="position:absolute;left:0;top:0;width:150px;height:150px;background:white;z-index:2">Overlay</div>
            <div style="position:absolute;left:200px;top:30px;width:0;height:0;overflow:hidden"><button aria-label="Like"><svg width="24" height="24"></svg></button></div>
            """);
        Assert.Null(await Site.Heart(page, "instagram", true, Target));
        Assert.Null(await Site.Heart(page, "instagram", false, Target));
    }

    [Fact, Trait("Category", "Browser")]
    public async Task TwoViewportControlsWithDifferentStatesRemainAmbiguous()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await Page(browser);
        await page.SetContentAsync("<button aria-label='Like'><svg width='24' height='24'></svg></button><button aria-label='Unlike'><svg width='24' height='24'></svg></button>");
        await Assert.ThrowsAsync<IOException>(() => Site.Heart(page, "instagram", false, Target));
        await Assert.ThrowsAsync<IOException>(() => Site.Heart(page, "instagram", true, Target));
    }
}
