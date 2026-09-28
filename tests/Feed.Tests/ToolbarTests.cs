using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
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
            foreach (var platform in new[] { "facebook", "instagram" })
                db.Posts.Add(new() { Platform = platform, PlatformPostId = "synthetic-toolbar", Text = "Synthetic toolbar fixture", PostedAt = Clock.Now });
            await db.SaveChangesAsync();
        }
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { WorkingDirectory = root!.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root.FullName, "src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"), "--data", instance.Paths.Root, "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!;
        var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { if ((await http.GetAsync("/healthz", timeout.Token)).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeout.Token);
            }
            using var pw = await Playwright.CreateAsync();
            await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = javascript, ViewportSize = new() { Width = 1280, Height = 900 } });
            var page = await context.NewPageAsync();
            await page.GotoAsync(http.BaseAddress.ToString());
            await Expect(page.Locator(".platforms .active")).ToHaveAttributeAsync("aria-label", "All platforms");
            Assert.Equal(4, await page.Locator(".platforms .active svg rect").CountAsync());
            Assert.Equal(0, await page.Locator(".platforms [aria-label='Instagram']").CountAsync());
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
            await page.GotoAsync(http.BaseAddress + "?view=sample&platform=instagram");
            await Expect(page.Locator(".platform-picker > summary")).ToHaveAttributeAsync("aria-label", "Platform: Instagram");
            await page.Locator(".platform-picker > summary").ClickAsync();
            await Expect(page.Locator(".platform-picker .active")).ToHaveClassAsync("active paused");
            await Expect(page.Locator(".platform-picker .active")).ToContainTextAsync("Instagram");
            await page.Locator(".platform-picker > summary").ClickAsync();
            Assert.True(await page.Locator(".view-label").EvaluateAsync<bool>("el => el.scrollWidth > el.clientWidth && getComputedStyle(el).textOverflow === 'ellipsis'"));
            await page.SetViewportSizeAsync(390, 844);
            await page.ScreenshotAsync(new() { Path = $"/tmp/feed-toolbar-phone-{javascript}.png" });
            await page.Locator(".view-picker > summary").ClickAsync();
            await Expect(page.Locator(".view-picker a[href='/debug']")).ToBeVisibleAsync();
            if (javascript)
            {
                await page.Keyboard.PressAsync("Escape");
                await Expect(page.Locator(".view-picker")).Not.ToHaveAttributeAsync("open", "");
                await page.Locator(".view-picker > summary").ClickAsync();
                await page.Locator(".page-footer").ClickAsync();
                await Expect(page.Locator(".view-picker")).Not.ToHaveAttributeAsync("open", "");
            }
            else await page.Locator(".view-picker > summary").ClickAsync();
            // A real plain POST queues work in this paused synthetic instance; the next render must report it.
            await page.Locator(".collect > summary").ClickAsync();
            await page.Locator(".collect form[action='/collect'] button").ClickAsync();
            await Expect(page.Locator(".collect > summary .mobile")).ToHaveTextAsync("Requested ✓");
            await page.Locator(".collect > summary").ClickAsync();
            await Expect(page.Locator(".collect form[action='/collect'] button")).ToBeDisabledAsync();
            await page.Locator(".collect > summary").ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Mark all read", Exact = true }).ClickAsync();
            await Expect(page.Locator(".toolbar-row")).ToBeVisibleAsync();
        }
        finally
        {
            if (!server.HasExited) server.Kill(true);
            await server.WaitForExitAsync(); await stdout; await stderr;
        }
    }
}
