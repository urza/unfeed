using System.Net;
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
        await using var host = await WebHost.Start(instance);
        Assert.Equal(HttpStatusCode.OK,(await host.Http.GetAsync("/manage?section=coverage")).StatusCode);Assert.Equal(HttpStatusCode.OK,(await host.Http.GetAsync("/manage?section=hearts")).StatusCode);
        using var pw=await Playwright.CreateAsync();await using var browser=await pw.Chromium.LaunchAsync(new(){Headless=true});
        await using(var context=await browser.NewContextAsync(new(){JavaScriptEnabled=false}))
        {
            var page=await context.NewPageAsync();await page.GotoAsync(host.BaseAddress.ToString());Assert.Equal(1,await page.Locator("a.manage-button:visible").CountAsync());Assert.Equal(0,await page.Locator("a[href*=backgrounds]").CountAsync());Assert.Contains("The feed is empty. Nothing collected yet.",await page.ContentAsync());
            long id;await using(var db=instance.Factory.Open()){var author=new Author{Platform="facebook",DisplayName="Synthetic Person",IsFriend=true,RefsJson="[\"fb:synthetic\"]"};db.Add(author);await db.SaveChangesAsync();var post=new Post{Platform="facebook",PlatformPostId="fixture",AuthorId=author.Id,Text="Synthetic caption <script>unsafe</script>",PostedAt=Clock.Now,SharedAuthor="Synthetic Original",SharedText="Original life update",SharedUrl="https://example.test/original",Summary="Synthetic Original is changing careers.",SummaryContentRevision=1};db.Add(post);await db.SaveChangesAsync();id=post.Id;}
            await page.ReloadAsync();
            await AssertSharedAttribution(page);
            await page.Locator(".expand-chip").ClickAsync();
            Assert.Contains("Synthetic caption",await page.Locator(".caption").InnerTextAsync());Assert.Equal(0,await page.Locator(".caption script").CountAsync());
            await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await using(var db=instance.Factory.Open()){Assert.Equal(1,await db.Feedback.CountAsync());Assert.False((await db.Posts.FindAsync(id))!.Hidden);}
            await page.GotoAsync(host.BaseAddress + "manage/backgrounds");
            Assert.EndsWith("/manage/backgrounds",page.Url);
            Assert.Equal("Backgrounds",await page.Locator(".manage-nav [aria-current=page]").InnerTextAsync());
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
            Assert.Equal(picture, await host.Http.GetByteArrayAsync(uploadedImage));
            await page.Locator(".background-gallery form[action$='/pin'] button").First.ClickAsync();
            Assert.Contains("pinned",await page.Locator(".background-gallery article").First.InnerTextAsync());
            using(var bad=new MultipartFormDataContent()) {
                bad.Add(new ByteArrayContent(picture),"images","wrong.jpg");
                var rejected=await host.Http.PostAsync("/manage/backgrounds/upload",bad);
                Assert.Equal(HttpStatusCode.BadRequest,rejected.StatusCode);
                Assert.Contains("No files were saved",await rejected.Content.ReadAsStringAsync());
            }
            using(var oversized=new MultipartFormDataContent()) {
                oversized.Add(new ByteArrayContent(new byte[23*1024*1024]),"images","large.png");
                using var request=new HttpRequestMessage(HttpMethod.Post,"/manage/backgrounds/upload"){Content=oversized};
                request.Headers.ExpectContinue=true;
                var rejected=await host.Http.SendAsync(request);
                Assert.True(rejected.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.RequestEntityTooLarge);
            }
            Assert.Equal(2,Directory.GetFiles(instance.Paths.Get("backgrounds","local")).Length);
            await page.SetViewportSizeAsync(390,844);
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
            await page.ScreenshotAsync(new(){Path="/tmp/feed-background-upload.png"});
            var css=await page.Locator("link[rel='stylesheet']").GetAttributeAsync("href");Assert.Equal(HttpStatusCode.OK,(await host.Http.GetAsync(css)).StatusCode);
        }
        await using(var context=await browser.NewContextAsync(new(){ViewportSize=new(){Width=390,Height=844}}))
        {
            var page=await context.NewPageAsync();await page.GotoAsync(host.BaseAddress.ToString());await page.WaitForSelectorAsync(".feed-grid.enhanced");await AssertSharedAttribution(page);Assert.Equal("/manage",await page.Locator("a.manage-button:visible").GetAttributeAsync("href"));Assert.Equal(1,await page.Locator(".feed-column").CountAsync());
            await page.Locator("form[data-action='thumb']").First.Locator("button").ClickAsync();await page.WaitForFunctionAsync("document.querySelector('[data-count]').textContent === '2'");
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth"));
        }
        Assert.Equal(HttpStatusCode.NotFound,(await host.Http.GetAsync("/media/%2e%2e/feed.db")).StatusCode);
    }
    static async Task AssertSharedAttribution(IPage page)
    {
        Assert.True(await page.Locator(".share-attribution").IsVisibleAsync());
        Assert.Equal("Synthetic Person shared a post by Synthetic Original", await page.Locator(".share-attribution").InnerTextAsync());
        Assert.Equal("https://example.test/original", await page.Locator(".share-attribution a").GetAttributeAsync("href"));
        Assert.False(await page.Locator(".text-body").IsVisibleAsync());
    }
}
