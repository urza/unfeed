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
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());Assert.Equal(1,await page.Locator("a.background-picker:visible").CountAsync());Assert.Contains("The feed is empty. Nothing collected yet.",await page.ContentAsync());
                long id;await using(var db=instance.Factory.Open()){var author=new Author{Platform="facebook",DisplayName="Synthetic Person",IsFriend=true,RefsJson="[\"fb:synthetic\"]"};db.Add(author);await db.SaveChangesAsync();var post=new Post{Platform="facebook",PlatformPostId="fixture",AuthorId=author.Id,Text="Synthetic caption <script>unsafe</script>",PostedAt=Clock.Now};db.Add(post);await db.SaveChangesAsync();id=post.Id;}
                await page.ReloadAsync();Assert.Contains("Synthetic caption",await page.Locator(".caption").InnerTextAsync());Assert.Equal(0,await page.Locator(".caption script").CountAsync());
                await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await using(var db=instance.Factory.Open()){Assert.Equal(1,await db.Feedback.CountAsync());Assert.False((await db.Posts.FindAsync(id))!.Hidden);}
                await page.GotoAsync(http.BaseAddress + "debug/backgrounds");
                Assert.Contains("data/backgrounds/local/", await page.ContentAsync());
                var picture = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
                await page.Locator("input[type=file]").SetInputFilesAsync(new[] {
                    new FilePayload { Name="synthetic-a.png", MimeType="image/png", Buffer=picture },
                    new FilePayload { Name="synthetic-b.png", MimeType="image/png", Buffer=picture }
                });
                await page.GetByRole(AriaRole.Button,new(){Name="Upload files",Exact=true}).ClickAsync();
                Assert.Contains("Uploaded 2 picture(s)", await page.Locator("[role=status]").InnerTextAsync());
                Assert.Equal(2,await page.Locator(".background-gallery article").CountAsync());
                var uploadedImage=await page.Locator(".background-gallery img").First.GetAttributeAsync("src");
                Assert.Equal(picture, await http.GetByteArrayAsync(uploadedImage));
                await page.Locator(".background-gallery form[action$='/pin'] button").First.ClickAsync();
                Assert.Contains("pinned",await page.Locator(".background-gallery article").First.InnerTextAsync());
                using(var bad=new MultipartFormDataContent()) {
                    bad.Add(new ByteArrayContent(picture),"images","wrong.jpg");
                    var rejected=await http.PostAsync("/debug/backgrounds/upload",bad);
                    Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
                    Assert.Contains("No files were saved",await rejected.Content.ReadAsStringAsync());
                }
                using(var oversized=new MultipartFormDataContent()) {
                    oversized.Add(new ByteArrayContent(new byte[23*1024*1024]),"images","large.png");
                    using var request=new HttpRequestMessage(HttpMethod.Post,"/debug/backgrounds/upload"){Content=oversized};
                    request.Headers.ExpectContinue=true;
                    var rejected=await http.SendAsync(request);
                    Assert.True(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
                }
                Assert.Equal(2,Directory.GetFiles(instance.Paths.Get("backgrounds","local")).Length);
                await page.SetViewportSizeAsync(390,844);
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
                await page.ScreenshotAsync(new(){Path="/tmp/feed-background-upload.png"});
                var css=await page.Locator("link[rel='stylesheet']").GetAttributeAsync("href");Assert.Equal(HttpStatusCode.OK,(await http.GetAsync(css)).StatusCode);
            }
            await using(var context=await browser.NewContextAsync(new(){ViewportSize=new(){Width=390,Height=844}}))
            {
                var page=await context.NewPageAsync();await page.GotoAsync(http.BaseAddress.ToString());await page.WaitForSelectorAsync(".feed-grid.enhanced");Assert.Equal("/debug/backgrounds",await page.Locator("a.background-picker:visible").GetAttributeAsync("href"));Assert.Equal(1,await page.Locator(".feed-column").CountAsync());
                await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await page.WaitForFunctionAsync("document.querySelector('[data-count]').textContent === '2'");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            }
            Assert.Equal(HttpStatusCode.NotFound,(await http.GetAsync("/media/%2e%2e/feed.db")).StatusCode);
        }
        finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();await stdout;await stderr;}
    }
    static string FindRoot(){var d=new DirectoryInfo(AppContext.BaseDirectory);while(d is not null&&!File.Exists(Path.Combine(d.FullName,"Feed.slnx")))d=d.Parent;return d?.FullName??throw new DirectoryNotFoundException();}
}
