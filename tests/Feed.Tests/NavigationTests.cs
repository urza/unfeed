using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;
public sealed class NavigationTests
{
    [Theory, Trait("Category", "Browser")]
    [InlineData(1280, 900)] [InlineData(390, 844)]
    public async Task FeedNavigationKeepsDocumentRestoresHistoryAndFallsBack(int width, int height)
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"},"ui":{"stack":{"min_posts":0}}} """);
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", DisplayName = "Synthetic Person", IsFriend = true }; db.Add(author); await db.SaveChangesAsync();
            for (var n = 0; n < 45; n++) db.Add(new Post { Platform = "facebook", PlatformPostId = "fixture-" + n, AuthorId = author.Id,
                Text = "Synthetic readable content\n" + new string('x', n * 4), PostedAt = Clock.Now.AddMinutes(-n), Hidden = n == 44 });
            await db.SaveChangesAsync();
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root != null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { WorkingDirectory = root!.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root.FullName, "src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"), "--data", i.Paths.Root, "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(arg);
        using var server = Process.Start(start)!; var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}") };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true) { if (server.HasExited) throw new Exception(await stderr); try { if ((await http.GetAsync("/healthz", timeout.Token)).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(100, timeout.Token); }
            using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true });
            await using var context = await browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
            var page = await context.NewPageAsync(); var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
            await page.GotoAsync(http.BaseAddress.ToString()); await page.WaitForSelectorAsync(".feed-grid.enhanced");
            await page.EvaluateAsync("window.documentSentinel = 42; window.scrollTo(0, 1200)");
            await page.WaitForFunctionAsync("history.state?.feedScroll > 1000");
            var scroll = await page.EvaluateAsync<double>("window.scrollY");
            // Dispatching the link avoids Playwright scrolling it into view before the click.
            await page.Locator("a.author").First.DispatchEventAsync("click", new { button = 0 });
            await page.WaitForFunctionAsync("location.pathname.startsWith('/people/') && !document.querySelector('main').hasAttribute('aria-busy')");
            Assert.Equal(42, await page.EvaluateAsync<int>("window.documentSentinel"));
            Assert.Equal(0, await page.EvaluateAsync<int>("window.scrollY"));
            Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('main.page')"));
            await page.GoBackAsync();
            await page.WaitForFunctionAsync("location.pathname === '/' && !document.querySelector('main').hasAttribute('aria-busy') && window.scrollY > 1000");
            Assert.InRange(Math.Abs(await page.EvaluateAsync<double>("window.scrollY") - scroll), 0, 2);
            await page.EvaluateAsync("""
                (() => { const a = document.createElement('a'); a.href = location.href; document.querySelector('main').append(a); a.click(); })()
                """);
            await page.WaitForFunctionAsync("!document.querySelector('main').hasAttribute('aria-busy')");
            Assert.InRange(Math.Abs(await page.EvaluateAsync<double>("window.scrollY") - scroll), 0, 2);
            await page.GoForwardAsync();
            await page.WaitForFunctionAsync("location.pathname.startsWith('/people/') && !document.querySelector('main').hasAttribute('aria-busy')");
            Assert.Equal(42, await page.EvaluateAsync<int>("window.documentSentinel"));
            // Delegated handlers survive repeated page replacement and do not accumulate.
            await page.Locator("form[data-action=thumb]").First.Locator("button").ClickAsync();
            await page.WaitForFunctionAsync("document.querySelector('[data-count]').textContent === '1'");
            await using (var db = i.Factory.Open()) Assert.Equal(1, await db.Feedback.CountAsync());
            Assert.Equal(1, await page.Locator("dialog.lightbox").CountAsync());
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            // A superseded slow response must never replace a more recent choice.
            await page.RouteAsync("**/?view=all&hidden=1", async route => { await Task.Delay(400); await route.ContinueAsync(); });
            await page.EvaluateAsync("""
                (() => { for (const href of ['/?view=all&hidden=1', '/?view=all']) {
                  const a = document.createElement('a'); a.href = href; document.querySelector('main').append(a); a.click();
                } })()
                """);
            await page.WaitForFunctionAsync("location.pathname === '/' && location.search === '?view=all' && !document.querySelector('main').hasAttribute('aria-busy')");
            Assert.Equal(44, await page.Locator("article.card").CountAsync());
            Assert.Equal(42, await page.EvaluateAsync<int>("window.documentSentinel"));
            await page.UnrouteAsync("**/?view=all&hidden=1");
            // Fetch failure falls back to a normal document request, retaining the requested URL.
            await page.RouteAsync("**/?view=all&hidden=1", async route => { if (route.Request.ResourceType == "fetch") await route.AbortAsync(); else await route.ContinueAsync(); });
            await page.EvaluateAsync("""
                (() => { const a = document.createElement('a'); a.href = '/?view=all&hidden=1'; document.querySelector('main').append(a); a.click(); })()
                """);
            await page.WaitForFunctionAsync("location.search.includes('hidden=1') && window.documentSentinel === undefined");
            Assert.Single(await page.Locator("article.card").AllAsync());
            await page.WaitForFunctionAsync("history.state?.feedNavigation === true");
            await page.EvaluateAsync("window.documentSentinel = 99");
            var historyLength = await page.EvaluateAsync<int>("history.length");
            await page.RouteAsync("**/?view=all", async route => { if (route.Request.ResourceType == "fetch") await route.AbortAsync(); else await route.ContinueAsync(); });
            await page.GoBackAsync();
            await page.WaitForFunctionAsync("location.search === '?view=all' && window.documentSentinel === undefined");
            Assert.Equal(historyLength, await page.EvaluateAsync<int>("history.length"));
            Assert.Empty(errors);
        }
        finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); await stdout; await stderr; }
    }
}
