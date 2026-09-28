using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
using Xunit;
namespace Feed.Tests;
public sealed class WebTests
{
    [Fact, Trait("Category","Browser")]
    public async Task EmptyAndSeededFeedWorksWithAndWithoutJavascript()
    {
        await using var instance=new TestInstance();await instance.Init();
        await File.WriteAllTextAsync(instance.Paths.Get("config.json"),"{\"platforms\":{\"facebook\":{}},\"scheduler\":{\"enabled\":false},\"backgrounds\":{\"source\":\"off\"}}");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();int port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var root=FindRoot();var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){UseShellExecute=false,WorkingDirectory=root,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{Path.Combine(root,"src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"),"--data",instance.Paths.Root,"--urls",$"http://127.0.0.1:{port}"})start.ArgumentList.Add(arg);
        using var process=Process.Start(start)!;var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            using var http=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}")};using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while(true){try{if((await http.GetAsync("/healthz",timeout.Token)).IsSuccessStatusCode)break;}catch(HttpRequestException){}await Task.Delay(100,timeout.Token);}
            Assert.Equal(HttpStatusCode.OK,(await http.GetAsync("/debug")).StatusCode);
            using var pw=await Playwright.CreateAsync();await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
            await using(var context=await browser.NewContextAsync(new(){JavaScriptEnabled=false}))
            {
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());Assert.Contains("The feed is empty. Nothing collected yet.",await page.ContentAsync());
                long id;await using(var db=instance.Factory.Open()){var author=new Author{Platform="facebook",DisplayName="Synthetic Person",IsFriend=true,RefsJson="[\"fb:synthetic\"]"};db.Add(author);await db.SaveChangesAsync();var post=new Post{Platform="facebook",PlatformPostId="fixture",AuthorId=author.Id,Text="Synthetic caption <script>unsafe</script>",PostedAt=Clock.Now};db.Add(post);await db.SaveChangesAsync();id=post.Id;}
                await page.ReloadAsync();Assert.Contains("Synthetic caption",await page.Locator(".caption").InnerTextAsync());Assert.Equal(0,await page.Locator(".caption script").CountAsync());
                await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await using(var db=instance.Factory.Open()){Assert.Equal(1,await db.Feedback.CountAsync());Assert.False((await db.Posts.FindAsync(id))!.Hidden);}
                var css=await page.Locator("link[rel='stylesheet']").GetAttributeAsync("href");Assert.Equal(HttpStatusCode.OK,(await http.GetAsync(css)).StatusCode);
            }
            await using(var context=await browser.NewContextAsync(new(){ViewportSize=new(){Width=390,Height=844}}))
            {
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());await page.WaitForSelectorAsync(".feed-grid.enhanced");Assert.Equal(1,await page.Locator(".feed-column").CountAsync());
                await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await page.WaitForFunctionAsync("document.querySelector('[data-count]').textContent === '2'");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            }
            Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync("/media/%2e%2e/feed.db")).StatusCode);
        }
        finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();await stdout;await stderr;}
    }
    static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Feed.slnx")))d=d.Parent;return d?.FullName??throw new DirectoryNotFoundException();}
}
