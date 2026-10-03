using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;

namespace Feed.Web;

public sealed record PersonPage(Author Author, FeedPage Feed, string? Category, bool Close, bool Muted, bool Always, bool HomeTimeline, string[] Views, TimelineVisit? Visit, RunRequest? Request, Run? Run, string? Blocked, string? ProfileUrl);

public sealed class People(DbFactory factory)
{
    public const int CollectionScrolls = 10;
    public static string? ProfileUrl(Author author)
    {
        if (!Platforms.All.Contains(author.Platform)) return null;
        var reference = Identity.UrlRef(author.Platform, author.Url);
        var key = reference?[(reference.IndexOf(':') + 1)..];
        if (key is null && author.Platform == "facebook" && author.PlatformAuthorId is { Length: > 0 } id && id.All(char.IsAsciiDigit)) key = id;
        if (key is null) return null;
        return author.Platform == "facebook" && key.All(char.IsAsciiDigit) ? "https://www.facebook.com/profile.php?id=" + key : Platforms.Home(author.Platform) + Uri.EscapeDataString(key) + "/";
    }
    static async Task<string?> Blocked(FeedDb db, Author author, InstanceSnapshot s, CancellationToken ct)
    {
        if (ProfileUrl(author) is null) return "No usable platform profile is stored for this person.";
        if (!s.Config.Scheduler.Enabled) return "Scheduling is paused. Resume it in Manage before collecting.";
        if (!s.Config.Enabled(author.Platform)) return "This platform is paused. Enable it in Manage before collecting.";
        if (await db.PlatformStates.AnyAsync(p => p.Platform == author.Platform && p.NeedsRelogin, ct)) return "This platform needs login. Open Manage to log in.";
        if (await db.Runs.AnyAsync(r => r.Platform == author.Platform && r.Kind == "collect" && r.Status == "running", ct)) return "A collection is already running on this platform.";
        if (await db.RunRequests.AnyAsync(r => r.Platform == author.Platform && r.Kind == "collect" && (r.Status == "pending" || r.Status == "claimed"), ct)) return "A collection is already queued on this platform.";
        return null;
    }
    public async Task<PersonPage?> Read(long id, InstanceSnapshot s, string? category, string scope, CancellationToken ct)
    {
        await using var db = factory.Open();
        var authors = await db.Authors.AsNoTracking().ToArrayAsync(ct);
        var author = authors.FirstOrDefault(a => a.Id == id); if (author is null) return null;
        var feed = await new FeedQuery(factory).Read(s, "all", author.Platform, scope, ct, id, category);
        var request = await db.RunRequests.AsNoTracking().Where(r => r.Kind == "collect" && r.PersonAuthorId == id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        var run = request is null ? null : await db.Runs.AsNoTracking().Where(r => r.RequestId == request.Id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        var visit = await db.TimelineVisits.AsNoTracking().Where(v => v.AuthorId == id).OrderByDescending(v => v.Id).FirstOrDefaultAsync(ct);
        bool Matches(IEnumerable<string> entries) => entries.Any(e => Identity.Resolve(e, authors).Any(a => a.Id == id));
        return new(author, feed, category, new RuleContext(author, authors, s).CloseFriend, s.Preferences.Mutes.Any(e => Identity.Matches(e, author)), Matches(s.Preferences.Shows), author.IsFriend && Matches(s.Config.Platform(author.Platform).HomeTimelineAuthors), s.Taxonomy.Views.Where(v => v.Authors is { } entries && Matches(entries)).Select(v => v.Label).ToArray(), visit, request, run, await Blocked(db, author, s, ct), ProfileUrl(author));
    }
    public async Task<string?> Collect(long id, InstanceSnapshot s, CancellationToken ct)
    {
        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        var author = await db.Authors.FindAsync([id], ct); if (author is null) throw new KeyNotFoundException();
        if (await Blocked(db, author, s, ct) is { } reason) return reason;
        var added = await Actions.EnsureRequest(db, "collect", author.Platform, "home", ct, personAuthorId: id, person: ProfileUrl(author));
        await tx.CommitAsync(ct);
        return added == 0 ? "A collection is already queued or running on this platform." : null;
    }
}
