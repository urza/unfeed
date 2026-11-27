using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Feed.Web;

public sealed record PersonPage(Author Author, FeedPage Feed, string? Category, bool Close, bool Muted, bool Always, bool HomeTimeline, string[] Views, TimelineVisit? Visit, RunRequest? Request, Run? Run, string? Blocked, string? ProfileUrl);

public sealed class People(DbFactory factory, FeedQuery? feedQuery = null)
{
    readonly FeedQuery query = feedQuery ?? new FeedQuery(factory);
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
        if (!s.Config.Enabled(author.Platform)) return "This platform is paused. Enable it in Manage before collecting.";
        if (await db.PlatformStates.AnyAsync(p => p.Platform == author.Platform && p.NeedsRelogin, ct)) return "This platform needs login. Open Manage to log in.";
        if (await db.Runs.AnyAsync(r => r.Platform == author.Platform && r.Kind == "collect" && r.Status == "running", ct)) return "A collection is already running on this platform.";
        if (await db.RunRequests.AnyAsync(r => r.Platform == author.Platform && r.Kind == "collect" && (r.Status == "pending" || r.Status == "claimed"), ct)) return "A collection is already queued on this platform.";
        return null;
    }
    static string? BlockedFromFeed(Author author, InstanceSnapshot s, FeedPage feed, string? profileUrl)
    {
        if (profileUrl is null) return "No usable platform profile is stored for this person.";
        if (!s.Config.Enabled(author.Platform)) return "This platform is paused. Enable it in Manage before collecting.";
        if (feed.States.Any(p => p.Platform == author.Platform && p.NeedsRelogin)) return "This platform needs login. Open Manage to log in.";
        if (feed.Runs.Any(r => r.Platform == author.Platform && r.Kind == "collect" && r.Status == "running")) return "A collection is already running on this platform.";
        if (feed.Requests.Any(r => r.Platform == author.Platform && r.Kind == "collect" && (r.Status == "pending" || r.Status == "claimed"))) return "A collection is already queued on this platform.";
        return null;
    }
    public async Task<PersonPage?> Read(long id, InstanceSnapshot s, string? category, string scope, CancellationToken ct)
    {
        await using var db = factory.Open();
        await db.Database.OpenConnectionAsync(ct);
        var authors = await SqliteDates.ReadAuthorsAsync((SqliteConnection)db.Database.GetDbConnection(), null, ct);
        var author = authors.FirstOrDefault(a => a.Id == id); if (author is null) return null;
        // Share the singleton FeedQuery (reusing diagnostics cache) and pass preloaded authors so we don't scan Authors twice.
        var feed = await query.Read(s, "all", author.Platform, scope, ct, id, category, null, authors);
        var request = await db.RunRequests.AsNoTracking().Where(r => r.Kind == "collect" && r.PersonAuthorId == id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        var run = request is null ? null : await db.Runs.AsNoTracking().Where(r => r.RequestId == request.Id).OrderByDescending(r => r.Id).FirstOrDefaultAsync(ct);
        var visit = await db.TimelineVisits.AsNoTracking().Where(v => v.AuthorId == id).OrderByDescending(v => v.Id).FirstOrDefaultAsync(ct);
        var resolved = new ResolvedRules(authors, s);
        bool Matches(IEnumerable<string> entries) => entries.Any(e => resolved.Resolve(e).Any(a => a.Id == id));
        var profileUrl = ProfileUrl(author);
        return new(author, feed, category, author.IsFriend && resolved.CloseFriend(author), resolved.Muted(author), resolved.Shown(author), author.IsFriend && Matches(s.Config.Platform(author.Platform).HomeTimelineAuthors), s.Taxonomy.Views.Where(v => v.Authors is { } entries && Matches(entries)).Select(v => v.Label).ToArray(), visit, request, run, BlockedFromFeed(author, s, feed, profileUrl), profileUrl);
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
