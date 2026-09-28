using Feed.Cli;
using Feed.Core.Domain;
using Microsoft.Playwright;
using Xunit;
namespace Feed.Tests;
public sealed class BrowserSessionTests
{
    [Fact, Trait("Category", "Browser")] public async Task FollowingNavigationWaitsForLinkAndOpensDialogInsteadOfNavigatingToGrid()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.SetContentAsync("""
            <main><article>Profile grid</article></main>
            <script>setTimeout(() => {
              const link = document.createElement('a'); link.href = '#'; link.textContent = '207 following';
              link.onclick = e => { e.preventDefault(); const dialog = document.createElement('div'); dialog.role = 'dialog'; dialog.textContent = 'Following'; document.body.append(dialog); };
              document.body.append(link);
            }, 100);</script>
            """);
        var dialog = await Site.OpenInstagramFollowing(page);
        Assert.True(await dialog.IsVisibleAsync()); Assert.Equal("about:blank", page.Url);
    }
    [Fact] public async Task FreshProfilesHaveNotificationBlockBeforeFirstLaunch()
    {
        await using var i = new TestInstance(); var profile = i.Paths.Get("profiles", "instagram");
        BrowserSession.NotificationPreferences(profile, "instagram");
        using var doc = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(profile, "Default", "Preferences")));
        Assert.Equal(2, doc.RootElement.GetProperty("profile").GetProperty("content_settings").GetProperty("exceptions").GetProperty("notifications").GetProperty("https://www.instagram.com:443,*").GetProperty("setting").GetInt32());
    }
    [Fact, Trait("Category", "Browser")] public async Task NotificationPromptDismissalIsScopedAndRejectsAmbiguity()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        foreach (var (heading, button) in new[] { ("Turn on Notifications", "Not Now"), ("Zapnout oznámení", "Teď ne") })
        {
            await page.SetContentAsync($"<button onclick='window.wrong=true'>Not Now</button><div role='dialog'><h2>{heading}</h2><button onclick='window.wrong=true'>Enable</button><button onclick='this.parentElement.remove()'>{button}</button></div>");
            await Site.DismissNotificationPrompt(page); Assert.Equal(0, await page.GetByRole(AriaRole.Dialog).CountAsync()); Assert.False(await page.EvaluateAsync<bool>("!!window.wrong"));
        }
        await page.SetContentAsync("<div role='dialog'><h2>Confirm your login</h2><button onclick='window.wrong=true'>Not Now</button></div>");
        await Site.DismissNotificationPrompt(page); Assert.Equal(1, await page.GetByRole(AriaRole.Dialog).CountAsync()); Assert.False(await page.EvaluateAsync<bool>("!!window.wrong"));
        await page.SetContentAsync("<div role='dialog'><h2>Turn on Notifications</h2><button onclick='window.wrong=true'>Not Now</button><button onclick='window.wrong=true'>Later</button></div>");
        await Assert.ThrowsAsync<IOException>(() => Site.DismissNotificationPrompt(page)); Assert.False(await page.EvaluateAsync<bool>("!!window.wrong"));
    }
    [Fact]public async Task NotificationPreferencesPreserveExistingDecision()
    {
        await using var i=new TestInstance();var profile=i.Paths.Get("profiles","facebook");Directory.CreateDirectory(Path.Combine(profile,"Default"));var file=Path.Combine(profile,"Default","Preferences");await File.WriteAllTextAsync(file,"{\"profile\":{\"content_settings\":{\"exceptions\":{\"notifications\":{\"https://www.facebook.com:443,*\":{\"setting\":1}}}}},\"unrelated\":17}");
        BrowserSession.NotificationPreferences(profile,"facebook");BrowserSession.NotificationPreferences(profile,"facebook");using var doc=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(file));Assert.Equal(17,doc.RootElement.GetProperty("unrelated").GetInt32());var notifications=doc.RootElement.GetProperty("profile").GetProperty("content_settings").GetProperty("exceptions").GetProperty("notifications");Assert.Equal(1,notifications.GetProperty("https://www.facebook.com:443,*").GetProperty("setting").GetInt32());Assert.Equal(2,notifications.GetProperty("https://www.fb.com:443,*").GetProperty("setting").GetInt32());
    }
    [Fact,Trait("Category","Browser")]public async Task HeadedSessionStagesProfileAndExportsCookiesAfterClose()
    {
        await using var i=new TestInstance();var config=new FeedConfig{Browser=new(){StageProfiles=true,StageDir=i.Paths.Get("stage"),NoSandbox=true,Display=":"+Random.Shared.Next(200,900)}};
        var originalDisplay=Environment.GetEnvironmentVariable("DISPLAY");
        for (var pass=0; pass<2; pass++)
        await using(var session=await BrowserSession.Open(i.Paths,"instagram",config,default))
        {
            await session.Page.RouteAsync("https://www.instagram.com/**", route => route.FulfillAsync(new() { ContentType = "text/html", Body = "<main><article>Synthetic page</article></main>" }));
            await session.Page.GotoAsync("https://www.instagram.com/"); Assert.Equal("denied", await session.Page.EvaluateAsync<string>("Notification.permission"));
            await session.Page.SetContentAsync("<main><article>Synthetic local page</article></main>");Assert.Equal(1,await session.Page.Locator("article").CountAsync());await session.Context.AddCookiesAsync([new Cookie{Name="synthetic",Value="fixture",Domain="example.test",Path="/",Secure=true}]);session.ExportCookiesOnClose();
        }
        Assert.Equal(originalDisplay,Environment.GetEnvironmentVariable("DISPLAY"));
        var cookie=i.Paths.Get("profiles","instagram","cookies.txt");Assert.Contains("synthetic\tfixture",await File.ReadAllTextAsync(cookie));Assert.True(Directory.Exists(i.Paths.Get("profiles","instagram","Default")));
        var pidFile=i.Paths.Get("locks","xvfb.pid");if(File.Exists(pidFile)){using var display=System.Diagnostics.Process.GetProcessById(int.Parse(await File.ReadAllTextAsync(pidFile)));display.Kill();await display.WaitForExitAsync();}
    }
    [Fact,Trait("Category","Browser")]public async Task InstagramHeartExcludesCommentAndRecognizesDoneState()
    {
        using var pw=await Playwright.CreateAsync();await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();await page.SetContentAsync("<button aria-label='Like' id='comment'><svg width='16'></svg></button><button aria-label='Unlike' id='post'><svg width='24'></svg></button>");Assert.Null(await Site.Heart(page,"instagram",false));Assert.Equal("post",await (await Site.Heart(page,"instagram",true))!.GetAttributeAsync("id"));
        await page.SetContentAsync("<button aria-label='Like'><svg width='24'></svg></button><button aria-label='Like'><svg width='24'></svg></button>");await Assert.ThrowsAsync<IOException>(()=>Site.Heart(page,"instagram",false));
    }
    [Fact, Trait("Category", "Browser")] public async Task FacebookOldPhotoPostLayoutRendersWithoutArticleOrH4()
    {
        using var pw = await Playwright.CreateAsync(); await using var browser = await pw.Chromium.LaunchAsync(new() { Headless = true }); var page = await browser.NewPageAsync();
        await page.SetContentAsync("<main role='main'><h1>Fixture Person</h1><a href='/fixture/photos'>Photos</a><a href='/fixture/friends'>Friends</a></main>");
        Assert.Equal(0, await page.Locator(Site.Selectors("facebook", true)).CountAsync());
        await page.SetContentAsync("<main role='main'><h2>Posts</h2><div><span>Fixture Person updated a profile picture</span><a href='/fixture/posts/101'>September 19</a><a href='/photo/?fbid=101'><img alt='Synthetic photo'></a></div></main>");
        Assert.True(await page.Locator(Site.Selectors("facebook", true)).CountAsync() > 0);
        Assert.Equal(0, await page.Locator(Site.Selectors("facebook", false)).CountAsync());
    }

}
