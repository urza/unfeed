using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit;

namespace Feed.Tests;
public sealed class ManagementBrowserTests
{
    [Theory, Trait("Category","Browser")]
    [InlineData(false)] [InlineData(true)]
    public async Task PeopleScheduleRulesAndConflictsWorkWithoutAutomaticRefresh(bool javascript)
    {
        await using var instance=new TestInstance();await instance.Init();
        await File.WriteAllTextAsync(instance.Paths.Get("config.json"),"""{"timezone":"UTC","platforms":{"facebook":{},"instagram":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"},"llm":{"api_key":"synthetic-do-not-render"}}""");
        await File.WriteAllTextAsync(instance.Paths.Get("taxonomy.json"),"""{"categories":[{"key":"life","label":"Life","definition":"Synthetic life updates"}],"views":[{"key":"life","label":"Life view","category":"life"},{"key":"selected","label":"Selected people","authors":["ig:101"]},{"key":"combined","label":"Combined","union":["life","selected"]}]}""");
        long id;
        await using(var db=instance.Factory.Open()) {
            var a=new Author { Platform="instagram",DisplayName="Synthetic Person",RefsJson="[\"ig:101\"]",IsFriend=true };db.Add(a);await db.SaveChangesAsync();id=a.Id;
            db.Add(new Post { Platform="instagram",PlatformPostId="fixture",AuthorId=id,Text="Synthetic post" });
            db.Add(new Post { Platform="instagram",PlatformPostId="retry-fixture",AuthorId=id,Text="Synthetic retry",IngestReadyAt=Clock.Now,LlmAttemptedAt=Clock.Now.AddHours(-1),LlmTokenLimit=4000,LlmFailures=1,LlmError="model token budget exhausted (max_tokens=4000)" });
            db.Add(new RawSnapshot {Platform="instagram",Path="raw/synthetic-warning.json",Parsed=true,Warning="location/profile_pic_url unavailable"});
            await db.SaveChangesAsync();
        }
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Feed.slnx")))root=root.Parent;
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet") {WorkingDirectory=root!.FullName,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{Path.Combine(root.FullName,"src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"),"--data",instance.Paths.Root,"--urls",$"http://127.0.0.1:{port}"})start.ArgumentList.Add(arg);
        start.Environment["FEED_CLI"]=instance.Paths.Get("missing-cli");
        using var server=Process.Start(start)!;var stdout=server.StandardOutput.ReadToEndAsync();var stderr=server.StandardError.ReadToEndAsync();
        try {
            var url=$"http://127.0.0.1:{port}";using var http=new HttpClient();using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while(true){if(server.HasExited)throw new InvalidOperationException("Web host exited: "+await stderr);try{if((await http.GetAsync(url+"/healthz",timeout.Token)).IsSuccessStatusCode)break;}catch(HttpRequestException){}await Task.Delay(100,timeout.Token);}
            using var pw=await Playwright.CreateAsync();await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});await using var context=await browser.NewContextAsync(new(){JavaScriptEnabled=javascript,ViewportSize=new(){Width=1280,Height=900}});
            var page=await context.NewPageAsync();await page.GotoAsync(url+"/manage");Assert.Equal(2,await page.Locator(".manage-platforms > section").CountAsync());Assert.Equal(0,await page.Locator("meta[http-equiv='refresh']").CountAsync());Assert.DoesNotContain("synthetic-do-not-render",await page.ContentAsync());
            Assert.True(await page.GetByRole(AriaRole.Button,new(){Name="Refresh following",Exact=true}).IsDisabledAsync());
            await page.GetByRole(AriaRole.Link,new(){Name="Backgrounds",Exact=true}).ClickAsync();Assert.EndsWith("/manage/backgrounds",page.Url);Assert.Equal("Backgrounds",await page.Locator(".manage-nav [aria-current=page]").InnerTextAsync());await page.GetByRole(AriaRole.Link,new(){Name="Overview",Exact=true}).ClickAsync();
            await page.GetByRole(AriaRole.Link,new(){Name="View recovery progress",Exact=true}).ClickAsync();
            Assert.Contains("Recovery is paused",await page.Locator(".recovery-summary").InnerTextAsync());
            var notices=page.Locator(".recovery-section").Filter(new(){HasText="Informational notices"});Assert.Null(await notices.GetAttributeAsync("open"));
            await page.GetByText("Show affected items and recovery instructions",new(){Exact=true}).First.ClickAsync();
            Assert.Contains("Collection association unavailable",await page.Locator(".recovery-details").InnerTextAsync());
            if(javascript){var updated=await page.Locator(".recovery-summary time").GetAttributeAsync("datetime");await page.WaitForFunctionAsync("previous => document.querySelector('.recovery-summary time').getAttribute('datetime') !== previous",updated,new(){Timeout=45000});Assert.True(await page.Locator(".recovery-group details").First.EvaluateAsync<bool>("e => e.open"));}
            await page.EvaluateAsync("document.querySelector('.background').style.background = 'repeating-linear-gradient(35deg, white 0px, white 24px, #ffdf00 24px, #ffdf00 48px, #ee4499 48px, #ee4499 72px)'");
            await page.ScreenshotAsync(new(){Path=$"/tmp/feed-recovery-desktop-{javascript}.png"});
            await page.SetViewportSizeAsync(390,844);Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            Assert.Equal("rgb(20, 33, 50)",await page.Locator(".recovery-summary").EvaluateAsync<string>("e => getComputedStyle(e).backgroundColor"));
            await page.ScreenshotAsync(new(){Path=$"/tmp/feed-recovery-phone-{javascript}.png"});await page.SetViewportSizeAsync(1280,900);
            var bad=await http.PostAsync(url+"/manage/settings/scheduler",new FormUrlEncodedContent(new Dictionary<string,string>{{"enabled","true"}}));Assert.Equal(HttpStatusCode.BadRequest,bad.StatusCode);
            await page.GotoAsync(url+"/manage?section=people&q=Synthetic&platform=instagram");var stale=await context.NewPageAsync();await stale.GotoAsync(page.Url);
            await page.GetByRole(AriaRole.Button,new(){Name="Close friend: Synthetic Person",Exact=true}).ClickAsync();Assert.Contains("q=Synthetic",page.Url);
            await using(var db=instance.Factory.Open()){Assert.False((await db.Posts.SingleAsync(p => p.PlatformPostId == "fixture")).Hidden);}
            Assert.Equal<string>(["ig:101"],new InstanceFiles(instance.Paths).Current.Config.Platform("instagram").CloseFriends);
            await stale.GetByRole(AriaRole.Button,new(){Name="Always show: Synthetic Person",Exact=true}).ClickAsync();Assert.Contains("Settings changed",await stale.Locator("[role=alert]").InnerTextAsync());
            await page.GetByRole(AriaRole.Button,new(){Name="Muted: Synthetic Person",Exact=true}).ClickAsync();await using(var db=instance.Factory.Open()){Assert.Equal("mute",(await db.Posts.SingleAsync(p => p.PlatformPostId == "fixture")).HiddenBy);}
            await page.GetByRole(AriaRole.Button,new(){Name="Muted: Synthetic Person",Exact=true}).ClickAsync();await using(var db=instance.Factory.Open()){Assert.False((await db.Posts.SingleAsync(p => p.PlatformPostId == "fixture")).Hidden);}
            await page.SetViewportSizeAsync(390,844);Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));await page.Locator(".people-table").ScrollIntoViewIfNeededAsync();await page.ScreenshotAsync(new(){Path=$"/tmp/feed-manage-people-{javascript}.png"});
            await page.GotoAsync(url+"/manage?section=schedule");var form=page.Locator("form[action='/manage/settings/schedule']").First;await form.Locator("input[name=times]").FillAsync("08:00, 18:30");await form.Locator("button").ClickAsync();
            Assert.Equal<string>(["08:00","18:30"],new InstanceFiles(instance.Paths).Current.Config.Platform("facebook").Schedule["home"]);Assert.False(new InstanceFiles(instance.Paths).Current.Config.Scheduler.Enabled);
            await page.Locator("[role=status]").WaitForAsync();Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));await page.ScreenshotAsync(new(){Path=$"/tmp/feed-manage-schedule-{javascript}.png"});
            await page.GotoAsync(url+"/manage?section=rules");await page.Locator("textarea[name=keywords]").FillAsync("Synthetic post");await page.GetByRole(AriaRole.Button,new(){Name="Save written rules",Exact=true}).ClickAsync();await using(var db=instance.Factory.Open()){Assert.Equal("keyword",(await db.Posts.SingleAsync(p => p.PlatformPostId == "fixture")).HiddenBy);}
            foreach(var section in new[]{"overview","categories","processing","storage"}){await page.GotoAsync(url+"/manage?section="+section);Assert.DoesNotContain("synthetic-do-not-render",await page.ContentAsync());Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));}
            await page.GotoAsync(url+"/manage?section=categories");await page.Locator("textarea[name=definition]").FillAsync("Synthetic changed definition");await page.GetByRole(AriaRole.Button,new(){Name="Save category",Exact=true}).ClickAsync();Assert.Equal("Synthetic changed definition",new InstanceFiles(instance.Paths).Current.Taxonomy.Categories[0].Definition);
            var viewForm=page.Locator("form[action='/manage/settings/view']").First;await viewForm.Locator("input[name=label]").FillAsync("Updated view");await viewForm.Locator("button").ClickAsync();Assert.Equal("Updated view",new InstanceFiles(instance.Paths).Current.Taxonomy.Views[0].Label);
            await page.SetViewportSizeAsync(1280,900);await page.GotoAsync(url+"/manage");await page.ScreenshotAsync(new(){Path=$"/tmp/feed-manage-overview-{javascript}.png"});
        } finally {if(!server.HasExited)server.Kill(true);await server.WaitForExitAsync();await stdout;await stderr;}
    }
}
