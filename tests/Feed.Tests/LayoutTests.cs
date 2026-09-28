using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Feed.Core.Domain;
using Microsoft.Playwright;
using Xunit;
using Xunit.Abstractions;
namespace Feed.Tests;
public sealed class LayoutTests(ITestOutputHelper output)
{
    [Fact,Trait("Category","Browser")]
    public async Task FiniteRenderBudgetExpansionGalleryAndLayoutMeasurements()
    {
        await using var i=new TestInstance();await i.Init();
        string Config(int cap)=>System.Text.Json.JsonSerializer.Serialize(new { platforms=new { facebook=new {} }, scheduler=new {enabled=false}, backgrounds=new {source="off"}, ui=new {render_cap=cap,stack=new {min_posts=0}} });
        await File.WriteAllTextAsync(i.Paths.Get("config.json"),Config(300));
        // Synthetic fixed-size media: geometry is explicit and no external URL is requested.
        await File.WriteAllBytesAsync(i.Paths.Get("media","fixture.png"),Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="));
        await using(var db=i.Factory.Open()){
            for(int n=0;n<420;n++){
                var p=new Post{Platform="facebook",PlatformPostId="fixture-"+n,ObservedAuthorName="Synthetic person "+n,Text="Synthetic caption with a link https://example.test/ and variable detail.\nSecond line.\nThird line.",PostedAt=Clock.Now.AddMinutes(-n),Summary=n%3==0?"Synthetic summary.":null,SummaryContentRevision=n%3==0?1:null};db.Posts.Add(p);
            }await db.SaveChangesAsync();
            var first=db.Posts.OrderBy(p=>p.Id).First();for(int n=0;n<4;n++)db.Media.Add(new(){PostId=first.Id,Path="media/fixture.png",Position=n,SourceKey="fixture"+n,Width=640,Height=480});await db.SaveChangesAsync();
        }
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"Feed.slnx")))root=root.Parent;
        var start=new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")??"dotnet"){WorkingDirectory=root!.FullName,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{Path.Combine(root.FullName,"src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"),"--data",i.Paths.Root,"--urls",$"http://127.0.0.1:{port}"})start.ArgumentList.Add(arg);
        using var server=Process.Start(start)!;var stdout=server.StandardOutput.ReadToEndAsync();var stderr=server.StandardError.ReadToEndAsync();
        try{
            using var http=new HttpClient{BaseAddress=new($"http://127.0.0.1:{port}")};using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while(true){try{if((await http.GetAsync("/healthz",timeout.Token)).IsSuccessStatusCode)break;}catch(HttpRequestException){}await Task.Delay(100,timeout.Token);}
            await http.GetStringAsync("/");var times=new List<double>();int bytes=0;
            for(int n=0;n<7;n++){using var response=await http.GetAsync("/");response.EnsureSuccessStatusCode();times.Add(double.Parse(response.Headers.GetValues("X-Render-Ms").Single(),System.Globalization.CultureInfo.InvariantCulture));bytes=(await response.Content.ReadAsByteArrayAsync()).Length;}
            times.Sort();output.WriteLine($"300 cards: warm server median={times[3]:F2} ms, max={times[^1]:F2} ms, HTML={bytes} bytes (7 requests, Debug build, loopback).");
            using var pw=await Playwright.CreateAsync();await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
            await using(var context=await browser.NewContextAsync(new(){JavaScriptEnabled=false,ViewportSize=new(){Width=1280,Height=900}})){
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());Assert.Equal(300,await page.Locator("article.card").CountAsync());Assert.DoesNotContain("@if",await page.Locator("body").InnerTextAsync());
                var first=page.Locator("article.card").First;Assert.False(await first.Locator(".text-body").IsVisibleAsync());await first.Locator(".expand-chip").ClickAsync();Assert.True(await first.Locator(".text-body").IsVisibleAsync());Assert.True(await first.Locator(".extra-media").IsVisibleAsync());
            }
            await using(var context=await browser.NewContextAsync(new(){ViewportSize=new(){Width=1280,Height=900}})){
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());await page.WaitForSelectorAsync(".feed-grid.enhanced");Assert.Equal(3,await page.Locator(".feed-column").CountAsync());
                var first=page.Locator("#post-1");var top=(await first.BoundingBoxAsync())!.Y;await first.Locator(".expand-chip").ClickAsync();Assert.InRange(Math.Abs((await first.BoundingBoxAsync())!.Y-top),0,1);
                await first.Locator(".more").ClickAsync();await page.WaitForSelectorAsync("dialog[open]");Assert.Equal("4 / 4",await page.Locator(".counter").InnerTextAsync());await page.Keyboard.PressAsync("ArrowRight");Assert.Equal("1 / 4",await page.Locator(".counter").InnerTextAsync());await page.Keyboard.PressAsync("Escape");Assert.Equal(0,await page.Locator("dialog[open]").CountAsync());Assert.True(await page.EvaluateAsync<bool>("document.activeElement.matches('[data-gallery] a')"));
                output.WriteLine("Desktop browser: "+await page.EvaluateAsync<string>("JSON.stringify({paint:performance.getEntriesByType('paint').map(x=>({name:x.name,ms:x.startTime})),dcl:performance.getEntriesByType('navigation')[0].domContentLoadedEventEnd})"));
                await page.ScreenshotAsync(new(){Path="/tmp/feed-v3-desktop.png"});
                await page.SetViewportSizeAsync(390,844);await page.WaitForFunctionAsync("document.querySelectorAll('.feed-column').length===1");Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth"));
            }
            await File.WriteAllTextAsync(i.Paths.Get("config.json"),Config(1000));using var raised=await http.GetAsync("/");var html=await raised.Content.ReadAsStringAsync();Assert.Equal(420,System.Text.RegularExpressions.Regex.Matches(html,"data-post=").Count);output.WriteLine($"Raised cap 1000, 420 matches: server={raised.Headers.GetValues("X-Render-Ms").Single()} ms, HTML={System.Text.Encoding.UTF8.GetByteCount(html)} bytes.");
            await using(var context=await browser.NewContextAsync(new(){ViewportSize=new(){Width=390,Height=844}})){var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());await page.WaitForSelectorAsync(".feed-grid.enhanced");Assert.Equal(420,await page.Locator("article.card").CountAsync());Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth<=innerWidth"));output.WriteLine("Phone raised cap: "+await page.EvaluateAsync<string>("JSON.stringify({paint:performance.getEntriesByType('paint').map(x=>({name:x.name,ms:x.startTime})),dcl:performance.getEntriesByType('navigation')[0].domContentLoadedEventEnd})"));}
        }
        finally{if(!server.HasExited)server.Kill(true);await server.WaitForExitAsync();await stdout;await stderr;}
    }
}
