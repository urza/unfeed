using Feed.Core.Domain;
using Microsoft.Playwright;
using Xunit;
using static Microsoft.Playwright.Assertions;

namespace Feed.Tests;

public sealed class ToolbarTests
{
    [Theory, Trait("Category", "Browser")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolbarAdaptsToContentWidthAndKeepsNativeControls(bool javascript)
    {
        await using var instance = new TestInstance();
        await instance.Init();
        await File.WriteAllTextAsync(instance.Paths.Get("config.json"), """
            {"platforms":{"facebook":{},"instagram":{"enabled":false}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}
            """);
        await File.WriteAllTextAsync(instance.Paths.Get("taxonomy.json"), """
            {"categories":[{"key":"sample","label":"Sample","definition":"Synthetic category"}],"views":[{"key":"sample","label":"A deliberately long synthetic view label for small screens","category":"sample"}]}
            """);
        await using (var db = instance.Factory.Open())
        {
            db.Runs.Add(new() { Platform = "facebook", Kind = "collect", Mode = "all_followed", Trigger = "recovery", Status = "error", FinishedAt = Clock.Now, StatsJson = "{\"visited\":5}" });
            await db.SaveChangesAsync();
            for (int n = 0; n < 25; n++) db.Runs.Add(new() { Kind = "process", Status = "ok", FinishedAt = Clock.Now });
            foreach (var platform in new[] { "facebook", "instagram" })
                db.Posts.Add(new() { Platform = platform, PlatformPostId = "synthetic-toolbar", Text = "Synthetic toolbar fixture", PostedAt = Clock.Now });
            await db.SaveChangesAsync();
        }
        // The paused host still dispatches the collect queued below; the held browser lock keeps that worker from starting.
        using var facebookLock = Feed.Core.Infrastructure.ResourceLock.Try(instance.Paths, "facebook");
        await using var host = await WebHost.Start(instance);
        using var pw = await Playwright.CreateAsync();
        await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
        await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = javascript, ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await context.NewPageAsync();
        await page.GotoAsync(host.BaseAddress.ToString());
        await Expect(page.Locator(".brand-sub")).ToContainTextAsync("facebook gap retry");
        await Expect(page.Locator(".brand-sub")).ToContainTextAsync("errors");
        await page.EvaluateAsync("document.querySelector('.background').style.background = 'repeating-linear-gradient(35deg,white 0 24px,#ffdf00 24px 48px,#ee4499 48px 72px)'");
        await Expect(page.Locator(".platforms .active")).ToHaveAttributeAsync("aria-label", "All platforms");
        Assert.Equal(4, await page.Locator(".platforms .active svg rect").CountAsync());
        Assert.Equal(0, await page.Locator(".platforms [aria-label='Instagram']").CountAsync());
        Assert.Equal("rgb(20, 33, 50)", await page.Locator(".feed-toolbar").EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor"));
        await page.ScreenshotAsync(new() { Path = $"/tmp/feed-toolbar-desktop-{javascript}.png" });
        // The switch follows the content box, even inside a wide viewport.
        await page.Locator(".page").EvaluateAsync("el => el.style.maxWidth = '737px'");
        await Expect(page.Locator(".toolbar-head")).ToBeHiddenAsync();
        await page.Locator(".page").EvaluateAsync("el => el.style.maxWidth = ''");
        foreach (var width in new[] { 1000, 738, 737, 480, 390, 320 })
        {
            await page.SetViewportSizeAsync(width, 900);
            if (width >= 738)
            {
                await Expect(page.Locator(".toolbar-head")).ToBeVisibleAsync();
                await Expect(page.Locator(".view-picker")).ToBeHiddenAsync();
            }
            else
            {
                await Expect(page.Locator(".toolbar-head")).ToBeHiddenAsync();
                await Expect(page.Locator(".views")).ToBeHiddenAsync();
                await Expect(page.Locator(".view-picker")).ToBeVisibleAsync();
                var toolbar = (await page.Locator(".feed-toolbar").BoundingBoxAsync())!;
                var row = (await page.Locator(".toolbar-row").BoundingBoxAsync())!;
                Assert.True(toolbar.Height <= row.Height + 18, $"Entire toolbar must fit one row at {width}px");
                await Expect(page.GetByRole(AriaRole.Link, new() { Name = "Manage", Exact = true })).ToBeVisibleAsync();
                var controls = await page.Locator(".toolbar-row").EvaluateAsync<bool>("el => { const boxes = [...el.children].filter(x => getComputedStyle(x).display !== 'none').map(x => x.getBoundingClientRect()); return boxes.every(b => Math.abs((b.top+b.bottom)/2 - (boxes[0].top+boxes[0].bottom)/2) < 1 && b.left >= 0 && b.right <= innerWidth); }");
                Assert.True(controls, $"Controls must fit one row at {width}px");
                foreach (var picker in new[] { ".view-picker", ".platform-picker", ".collect" })
                {
                    await page.Locator(picker + " > summary").ClickAsync();
                    var panel = (await page.Locator(picker + " > .menu").BoundingBoxAsync())!;
                    Assert.True(panel.X >= 0 && panel.X + panel.Width <= width, $"{picker} panel must fit at {width}px");
                    await page.Locator(picker + " > summary").ClickAsync();
                }
            }
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        }
        await page.GotoAsync(host.BaseAddress + "?view=sample&platform=instagram");
        await Expect(page.Locator(".platform-picker > summary")).ToHaveAttributeAsync("aria-label", "Platform: Instagram");
        await page.Locator(".platform-picker > summary").ClickAsync();
        await Expect(page.Locator(".platform-picker .active")).ToHaveClassAsync("active paused");
        await Expect(page.Locator(".platform-picker .active")).ToContainTextAsync("Instagram");
        await page.Locator(".platform-picker > summary").ClickAsync();
        Assert.True(await page.Locator(".view-label").EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth && getComputedStyle(el).textOverflow === 'ellipsis'"));
        await page.SetViewportSizeAsync(390, 844);
        await page.EvaluateAsync("document.querySelector('.background').style.background = 'repeating-linear-gradient(35deg,white 0 24px,#ffdf00 24px 48px,#ee4499 48px 72px)'");
        await page.ScreenshotAsync(new() { Path = $"/tmp/feed-toolbar-phone-{javascript}.png" });
        await page.Locator(".view-picker > summary").ClickAsync();
        await Expect(page.Locator("a.manage-button.mobile")).ToBeVisibleAsync();
        if (javascript)
        {
            await page.Keyboard.PressAsync("Escape");
            await Expect(page.Locator(".view-picker")).Not.ToHaveAttributeAsync("open", "");
            await page.Locator(".view-picker > summary").ClickAsync();
            await page.Locator(".page-footer").ClickAsync();
            await Expect(page.Locator(".view-picker")).Not.ToHaveAttributeAsync("open", "");
        }
        else await page.Locator(".view-picker > summary").ClickAsync();
        // A real plain POST queues work; the held browser lock keeps it waiting, and the next render must report it.
        await page.Locator(".collect > summary").ClickAsync();
        await page.Locator(".collect form[action='/collect'] button").ClickAsync();
        await Expect(page.Locator(".collect > summary .mobile")).ToHaveTextAsync("Requested ✓");
        await page.SetViewportSizeAsync(320, 900);
        Assert.True(await page.Locator(".toolbar-row").EvaluateAsync<bool>("el => { const boxes = [...el.children].filter(x => getComputedStyle(x).display !== 'none').map(x => x.getBoundingClientRect()); return boxes.every((b, i) => b.left >= 0 && b.right <= innerWidth && (i === 0 || b.left >= boxes[i - 1].right)); }"), "Queued controls must fit without overlapping at 320px");
        await page.Locator(".collect > summary").ClickAsync();
        await Expect(page.Locator(".collect form[action='/collect'] button")).ToBeDisabledAsync();
        await page.Locator(".collect > summary").ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).ClickAsync();
        await Expect(page.Locator(".toolbar-row")).ToBeVisibleAsync();
    }
}
