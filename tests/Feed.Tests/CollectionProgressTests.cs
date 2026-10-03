using Feed.Cli;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;
public sealed class CollectionProgressTests
{
    [Fact] public void KnownHistoryDoesNotStopPersonVisitsButStillStopsOrdinaryCollection()
    {
        var person = new CollectionProgress(true); var home = new CollectionProgress(false);
        for (int n = 0; n < 10; n++) Assert.Null(person.Observe(false, false, true));
        Assert.Null(home.Observe(false, true, true)); Assert.Null(home.Observe(false, true, true)); Assert.Equal("no_new", home.Observe(false, true, true));
    }
    [Fact] public void ThreeActualStallsStopAndEitherPageOrPostProgressResetsTheStreak()
    {
        var progress = new CollectionProgress(true);
        Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(false, false, false));
        Assert.Null(progress.Observe(false, true, false)); // another stored post loads without movement
        Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(false, false, false));
        Assert.Null(progress.Observe(false, false, true)); // moving through a tall existing post
        Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(false, false, false)); Assert.Equal("stalled", progress.Observe(false, false, false));
    }
    [Fact] public void OrdinaryCollectionStillResetsOnDatabaseFreshPosts()
    {
        var progress = new CollectionProgress(false);
        Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(true, true, false));
        Assert.Null(progress.Observe(false, false, false)); Assert.Null(progress.Observe(false, false, false)); Assert.Equal("no_new", progress.Observe(false, false, false));
    }
    [Fact, Trait("Category", "Browser")]
    public async Task RealScrollingContinuesPastThreeKnownStepsThenDetectsBottomAndLateGrowth()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        var page = await browser.NewPageAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        await page.SetContentAsync("<style>body{margin:0}article{height:900px}</style><article>Known synthetic post</article><article>Known synthetic post</article><article>Known synthetic post</article><article>Known synthetic post</article><article>Older synthetic post</article><article>End</article>");
        var progress = new CollectionProgress(true);
        for (int n = 0; n < 4; n++) {
            var before = await ScrollPosition.Read(page); await page.Mouse.WheelAsync(0, 900); await page.WaitForFunctionAsync("before => scrollY > before", before.Top);
            Assert.Null(progress.Observe(false, false, (await ScrollPosition.Read(page)).AdvancedFrom(before)));
        }
        await page.EvaluateAsync("scrollTo(0,document.body.scrollHeight)");
        for (int n = 0; n < 3; n++) { var before = await ScrollPosition.Read(page); await page.Mouse.WheelAsync(0, 900); await page.WaitForTimeoutAsync(100); var stop = progress.Observe(false, false, (await ScrollPosition.Read(page)).AdvancedFrom(before)); Assert.Equal(n == 2 ? "stalled" : null, stop); }
        var old = await ScrollPosition.Read(page); await page.EvaluateAsync("document.body.insertAdjacentHTML('beforeend','<article>Late synthetic post</article>')");
        Assert.True((await ScrollPosition.Read(page)).AdvancedFrom(old));
    }
}
