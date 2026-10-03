using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public sealed class Actions(DbFactory factory)
{
    public static async Task<int> EnsureRequest(FeedDb db, string kind, string? platform, string? mode = null, CancellationToken ct = default, bool retryIncomplete = false, long? personAuthorId = null, string? person = null)
    {
        if ((person is not null || personAuthorId is not null) && (person is null || personAuthorId is null || kind != "collect" || mode != "home" || retryIncomplete || platform is null || !Platforms.All.Contains(platform) || Identity.UrlRef(platform, person) is null)) throw new ArgumentException("Invalid person collection target");
        if (kind == "collect" && await db.Runs.AnyAsync(r => r.Kind == "collect" && r.Platform == platform && r.Status == "running", ct)) return 0;
        return await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO RunRequests (Kind,Platform,Mode,Status,RequestedAt,RetryIncomplete,PersonAuthorId,Person) SELECT {kind},{platform},{mode},'pending',{Clock.Now},{retryIncomplete},{personAuthorId},{person} WHERE NOT EXISTS (SELECT 1 FROM RunRequests WHERE Kind={kind} AND Platform IS {platform} AND Status IN ('pending','claimed'))", ct);
    }
    public async Task Collect(string platform, string mode, CancellationToken ct = default) { if (!Platforms.All.Contains(platform) || !Platforms.Modes.Contains(Platforms.Mode(mode))) throw new ArgumentException("Unknown platform or mode"); await using var db = factory.Open(); await EnsureRequest(db, "collect", platform, Platforms.Mode(mode), ct); }
    public async Task<string?> QueueLike(long id, InstanceSnapshot instance, bool force = false, CancellationToken ct = default)
    {
        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync(ct); var p = await db.Posts.FindAsync([id], ct);
        if (p is null) return "post does not exist";
        if (!Platforms.All.Contains(p.Platform) || string.IsNullOrWhiteSpace(p.Permalink) || string.IsNullOrWhiteSpace(p.LikeRef)) return "post has no usable like handle and permalink";
        if (!instance.Config.Platform(p.Platform).Likeback) return $"like-back is not enabled for {p.Platform} (platforms.{p.Platform}.likeback)";
        if (force || !await db.Likes.AnyAsync(l => l.PostId == id && l.State == "sent", ct))
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO Likes (PostId,Platform,State,RequestedAt) VALUES ({id},{p.Platform},'pending',{Clock.Now})", ct);
            await EnsureRequest(db, "like", p.Platform, ct: ct);
        }
        await tx.CommitAsync(ct); return null;
    }
    public async Task<bool> Thumb(long id, int value, string? view, string scope, CancellationToken ct = default)
    {
        if (value is not (1 or -1) || scope is not ("live" or "hidden" or "unsorted")) return false;
        await using var db = factory.Open(); var p = await db.Posts.FindAsync([id], ct); if (p is null) return false;
        db.Feedback.Add(new() { PostId = id, Value = value, Platform = p.Platform, AuthorId = p.AuthorId, ViewKey = view, Scope = scope, CategoriesAtVote = p.CategoriesJson, ScoreAtVote = p.LlmScore, ReasonAtVote = p.LlmReason }); await db.SaveChangesAsync(ct); return true;
    }
    public async Task<bool> Unhide(long id, CancellationToken ct = default) { await using var db = factory.Open(); return await db.Posts.Where(p => p.Id == id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Hidden, false).SetProperty(p => p.HiddenBy, (string?)null).SetProperty(p => p.HiddenReason, (string?)null).SetProperty(p => p.HiddenAt, (DateTime?)null).SetProperty(p => p.VisibilityRevision, p => p.VisibilityRevision + 1), ct) > 0; }
    public async Task MarkRead(CancellationToken ct = default) { await using var db = factory.Open(); await db.Put("last_visit", Clock.Now.ToString("O"), ct); }
}
