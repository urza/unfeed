using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;
public sealed class PersonBrowserTests
{
    [Theory, Trait("Category", "Browser")]
    [InlineData(false)] [InlineData(true)]
    public async Task PersonNavigationCategoriesAndCollectionFormWorkOnDesktopAndPhone(bool javascript)
    {
        await using var i = new TestInstance(); await i.Init();
        // Keep the synthetic host paused. The POST path is separately exercised after
        // enabling dispatch while holding its ownership gates; no browser worker starts.
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}""");
        await File.WriteAllTextAsync(i.Paths.Get("taxonomy.json"), """{"categories":[{"key":"life","label":"Life","definition":"Synthetic life updates"}],"views":[]}""");
        long id;
        await using (var db = i.Factory.Open()) {
            var author = new Author { Platform = "facebook", DisplayName = "Synthetic Person", Url = "https://www.facebook.com/synthetic/", IsFriend = true, RefsJson = "[\"fb:synthetic\"]" }; db.Add(author); await db.SaveChangesAsync(); id = author.Id;
            db.AddRange(new Post { Platform = "facebook", PlatformPostId = "visible", AuthorId = id, Text = "Visible synthetic post", CategoriesJson = "[\"life\"]", VerdictContentRevision = 1, PostedAt = Clock.Now },
                new Post { Platform = "facebook", PlatformPostId = "unsorted", AuthorId = id, Text = "Unsorted synthetic post", PostedAt = Clock.Now },
                new Post { Platform = "facebook", PlatformPostId = "hidden", AuthorId = id, Text = "Hidden synthetic post", Hidden = true, HiddenBy = "keyword", HiddenReason = "Synthetic rule", PostedAt = Clock.Now }); await db.SaveChangesAsync();
        }
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var root = new DirectoryInfo(AppContext.BaseDirectory); while (root is not null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { WorkingDirectory = root!.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root.FullName, "src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"), "--data", i.Paths.Root, "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(arg);
        start.Environment["FEED_CLI"] = i.Paths.Get("missing-cli");
        using var server = Process.Start(start)!; var stdout = server.StandardOutput.ReadToEndAsync(); var stderr = server.StandardError.ReadToEndAsync();
        try {
            var url = $"http://127.0.0.1:{port}"; using var http = new HttpClient(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true) { if (server.HasExited) throw new InvalidOperationException(await stderr); try { if ((await http.GetAsync(url + "/healthz", timeout.Token)).IsSuccessStatusCode) break; } catch (HttpRequestException) { } await Task.Delay(100, timeout.Token); }
            using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = javascript, ViewportSize = new() { Width = 1280, Height = 900 } });
            var page = await context.NewPageAsync(); await page.GotoAsync(url);
            var link = page.Locator(".card-head .author").First; Assert.Equal($"/people/{id}", await link.GetAttributeAsync("href")); Assert.Null(await link.GetAttributeAsync("target")); await link.ClickAsync();
            Assert.Equal("Synthetic Person", await page.Locator("h1").InnerTextAsync()); Assert.Equal(2, await page.Locator(".card").CountAsync());
            Assert.Equal("https://www.facebook.com/synthetic/", await page.GetByRole(AriaRole.Link, new() { Name = "Open platform profile" }).GetAttributeAsync("href"));
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsDisabledAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "Life", Exact = true }).ClickAsync(); Assert.Equal(1, await page.Locator(".card").CountAsync()); Assert.Contains("Visible synthetic", await page.Locator(".card").InnerTextAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "Unsorted (1)", Exact = true }).ClickAsync(); Assert.Contains("Unsorted synthetic", await page.Locator(".card").InnerTextAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "Hidden (1)", Exact = true }).ClickAsync(); Assert.Contains("Synthetic rule", await page.Locator(".hidden-tag").InnerTextAsync());
            await page.GetByRole(AriaRole.Link, new() { Name = "All posts", Exact = true }).ClickAsync();
            await page.Locator("form[data-action=thumb]").First.Locator("button").ClickAsync();
            await Assertions.Expect(page.Locator("form[data-action=thumb]").First.Locator("[data-count]")).ToHaveTextAsync("1"); await using (var db = i.Factory.Open()) Assert.Equal(1, await db.Feedback.CountAsync());
            await page.SetViewportSizeAsync(390, 844);
            if (javascript) await page.WaitForFunctionAsync("document.querySelectorAll('.feed-column').length === 1");
            Assert.True((await page.Locator(".card").First.BoundingBoxAsync())!.Width >= 340);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new() { Path = $"/tmp/feed-person-{javascript}.png", FullPage = true });
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync(url + "/people/99999")).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync(url + $"/people/{id}/collect", new FormUrlEncodedContent([]))).StatusCode);
            using var platformLock = Feed.Core.Infrastructure.ResourceLock.Try(i.Paths, "facebook"); using var processingLock = Feed.Core.Infrastructure.ResourceLock.Try(i.Paths, "processing");
            await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":true},"backgrounds":{"source":"off"}}""");
            await page.ReloadAsync(); Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsEnabledAsync());
            await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).ClickAsync(); Assert.Contains("Collection queued for this person", await page.Locator("[role=status]").InnerTextAsync());
            await using (var db = i.Factory.Open()) { var request = await db.RunRequests.SingleAsync(r => r.Kind == "collect"); Assert.Equal(id, request.PersonAuthorId); Assert.Equal("https://www.facebook.com/synthetic/", request.Person); }
            Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsDisabledAsync());
        } finally { if (!server.HasExited) server.Kill(true); await server.WaitForExitAsync(); await stdout; await stderr; }
    }
}
