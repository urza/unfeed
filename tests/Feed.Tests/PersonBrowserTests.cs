using System.Net;
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
        // The synthetic host stays paused. Owner-queued requests dispatch anyway, so the
        // ownership locks below are held before any POST; no browser worker starts.
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}""");
        await File.WriteAllTextAsync(i.Paths.Get("taxonomy.json"), """{"categories":[{"key":"life","label":"Life","definition":"Synthetic life updates"}],"views":[]}""");
        long id;
        await using (var db = i.Factory.Open()) {
            var author = new Author { Platform = "facebook", DisplayName = "Synthetic Person", Url = "https://www.facebook.com/synthetic/", IsFriend = true, RefsJson = "[\"fb:synthetic\"]" }; db.Add(author); await db.SaveChangesAsync(); id = author.Id;
            db.AddRange(new Post { Platform = "facebook", PlatformPostId = "visible", AuthorId = id, Text = "Visible synthetic post", CategoriesJson = "[\"life\"]", VerdictContentRevision = 1, PostedAt = Clock.Now },
                new Post { Platform = "facebook", PlatformPostId = "unsorted", AuthorId = id, Text = "Unsorted synthetic post", PostedAt = Clock.Now },
                new Post { Platform = "facebook", PlatformPostId = "hidden", AuthorId = id, Text = "Hidden synthetic post", Hidden = true, HiddenBy = "keyword", HiddenReason = "Synthetic rule", PostedAt = Clock.Now }); await db.SaveChangesAsync();
        }
        await using var host = await WebHost.Start(i, s => s.Environment["FEED_CLI"] = i.Paths.Get("missing-cli"));
        var url = host.BaseAddress.ToString().TrimEnd('/');
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); await using var context = await browser.NewContextAsync(new() { JavaScriptEnabled = javascript, ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await context.NewPageAsync(); await page.GotoAsync(url);
        async Task Navigate(string name)
        {
            var target = page.GetByRole(AriaRole.Link, new() { Name = name, Exact = true });
            var href = await target.GetAttributeAsync("href");
            await target.ClickAsync();
            await Assertions.Expect(page).ToHaveURLAsync(url + href);
        }
        var link = page.Locator(".card-head .author").First; Assert.Equal($"/people/{id}", await link.GetAttributeAsync("href")); Assert.Null(await link.GetAttributeAsync("target")); await link.ClickAsync();
        Assert.Equal("Synthetic Person", await page.Locator("h1").InnerTextAsync()); Assert.Equal(2, await page.Locator(".card").CountAsync());
        Assert.Equal("https://www.facebook.com/synthetic/", await page.GetByRole(AriaRole.Link, new() { Name = "Open platform profile" }).GetAttributeAsync("href"));
        // The pause alone no longer disables the button; only the gates checked further down do.
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsEnabledAsync());
        await Navigate("Life"); Assert.Equal(1, await page.Locator(".card").CountAsync()); Assert.Contains("Visible synthetic", await page.Locator(".card").InnerTextAsync());
        await Navigate("Unsorted (1)"); Assert.Contains("Unsorted synthetic", await page.Locator(".card").InnerTextAsync());
        await Navigate("Hidden (1)"); Assert.Contains("Synthetic rule", await page.Locator(".hidden-tag").InnerTextAsync());
        await Navigate("All posts");
        await page.Locator("form[data-action=thumb]").First.Locator("button").ClickAsync();
        await Assertions.Expect(page.Locator("form[data-action=thumb]").First.Locator("[data-count]")).ToHaveTextAsync("1"); await using (var db = i.Factory.Open()) Assert.Equal(1, await db.Feedback.CountAsync());
        await page.SetViewportSizeAsync(390, 844);
        if (javascript) await page.WaitForFunctionAsync("document.querySelectorAll('.feed-column').length === 1");
        Assert.True((await page.Locator(".card").First.BoundingBoxAsync())!.Width >= 340);
        Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        await page.ScreenshotAsync(new() { Path = $"/tmp/feed-person-{javascript}.png", FullPage = true });
        Assert.Equal(HttpStatusCode.NotFound, (await host.Http.GetAsync(url + "/people/99999")).StatusCode);
        using var platformLock = Feed.Core.Infrastructure.ResourceLock.Try(i.Paths, "facebook"); using var processingLock = Feed.Core.Infrastructure.ResourceLock.Try(i.Paths, "processing");
        Assert.Equal(HttpStatusCode.BadRequest, (await host.Http.PostAsync(url + $"/people/{id}/collect", new FormUrlEncodedContent([]))).StatusCode);
        await page.ReloadAsync(); Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsEnabledAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).ClickAsync(); Assert.Contains("Collection queued for this person", await page.Locator("[role=status]").InnerTextAsync());
        await using (var db = i.Factory.Open()) { var request = await db.RunRequests.SingleAsync(r => r.Kind == "collect"); Assert.Equal(id, request.PersonAuthorId); Assert.Equal("https://www.facebook.com/synthetic/", request.Person); }
        Assert.True(await page.GetByRole(AriaRole.Button, new() { Name = "Collect latest posts" }).IsDisabledAsync());
    }
}
