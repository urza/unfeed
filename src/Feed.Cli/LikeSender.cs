using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Playwright;
namespace Feed.Cli;
public sealed class LikeSender(InstancePaths paths, DbFactory factory, Capture capture)
{
    public async Task<int> Send(string platform, InstanceSnapshot s, long? postId, CancellationToken ct)
    {
        // Recovery requires browser ownership; no uncertain write is ever automatically repeated.
        using (var held = ResourceLock.Try(paths, platform) ?? throw new ResourceBusyException("platform busy"))
        {
            await using var db = factory.Open();
            await db.Likes.Where(l => l.Platform == platform && l.State == "pending" && l.AttemptedAt != null).ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, "outcome unknown; inspect the original before retrying"), ct);
            if (!await db.Likes.AnyAsync(l => l.Platform == platform && l.State == "pending" && (postId == null || l.PostId == postId), ct)) return 0;
            if (!s.Config.Platform(platform).Likeback) { await db.Likes.Where(l => l.Platform == platform && l.State == "pending").ExecuteUpdateAsync(u => u.SetProperty(l => l.State, "failed").SetProperty(l => l.Error, $"like-back is not enabled for {platform} (platforms.{platform}.likeback)"), ct); return 1; }
            if (await db.PlatformStates.AnyAsync(p => p.Platform == platform && p.NeedsRelogin, ct)) return 75;
        }
        await using var session = await BrowserSession.Open(paths, platform, s.Config, ct); var page = session.Page; int failed = 0;
        for (int sweep = 0; sweep < (postId is null ? 3 : 1); sweep++)
        {
            await using var db = factory.Open(); var queue = await db.Likes.Where(l => l.Platform == platform && l.State == "pending" && (postId == null || l.PostId == postId)).OrderBy(l => l.Id).ToListAsync(ct);
            foreach (var like in queue)
            {
                var post = await db.Posts.FindAsync([like.PostId], ct);
                if (post?.Permalink is null || post.LikeRef is null) { like.State = "failed"; like.Error = "post has no usable like handle and permalink"; failed++; await db.SaveChangesAsync(ct); continue; }
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(s.Config.Likeback.MinDelaySeconds + Random.Shared.NextDouble() * (s.Config.Likeback.MaxDelaySeconds - s.Config.Likeback.MinDelaySeconds)), ct);
                    await page.GotoAsync(post.Permalink, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30000 });
                    if (await Site.Checkpoint(page, platform)) { await capture.Relogin(platform, true, ct); return 75; }
                    ILocator? button = null; bool done = false;
                    for (int n = 0; n < 10; n++) { if (await Site.Checkpoint(page, platform)) { await capture.Relogin(platform, true, ct); return 75; } await Site.DismissNotificationPrompt(page); if (await Site.Heart(page, platform, true) is not null) { done = true; break; } button = await Site.Heart(page, platform, false); if (button is not null) break; await Task.Delay(1000, ct); }
                    if (!done)
                    {
                        if (button is null) throw new IOException("like button not found");
                        if (platform == "facebook") { await button.HoverAsync(); button = page.GetByRole(AriaRole.Button, new() { Name = "Love", Exact = true }); await button.WaitForAsync(new() { Timeout = 4000 }); if (await button.CountAsync() != 1) throw new IOException("ambiguous Love control"); }
                        if (await Site.Checkpoint(page, platform)) { await capture.Relogin(platform, true, ct); return 75; }
                        like.AttemptedAt = Clock.Now; await db.SaveChangesAsync(ct); await button.ClickAsync();
                        for (int n = 0; n < 6; n++) { if (await Site.Checkpoint(page, platform)) { await capture.Relogin(platform, true, ct); like.State = "failed"; like.Error = "outcome unknown; inspect the original before retrying"; await db.SaveChangesAsync(ct); return 75; } await Site.DismissNotificationPrompt(page); if (await Site.Heart(page, platform, true) is not null) { done = true; break; } await Task.Delay(1000, ct); }
                        if (!done) throw new IOException("intended heart not confirmed for control '" + (await button.GetAttributeAsync("aria-label") ?? await button.InnerTextAsync()) + "'");
                    }
                    like.State = "sent"; like.SentAt = Clock.Now; like.Error = null;
                }
                catch (Exception e) when (e is not OperationCanceledException) { like.State = "failed"; like.Error = (like.AttemptedAt is not null ? "outcome unknown; inspect the original before retrying: " : "") + e.Message; failed++; }
                await db.SaveChangesAsync(ct);
            }
            if (postId is null && sweep < 2) await Task.Delay(3000, ct);
        }
        session.ExportCookiesOnClose(); return failed > 0 ? 1 : 0;
    }
}
