using System.Collections.Immutable;
using Feed.Core.Domain;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;
public sealed class FeedPerformanceTests
{
    static InstanceSnapshot Snapshot => new(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()) }, new(), Preferences.Parse(""));

    [Fact] public async Task SqlSelectionCountsUncappedScopesAndOrdersBeforeLoadingContent()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", RefsJson = "[\"fb:fixture\"]" }; db.Add(author); await db.SaveChangesAsync();
            for (var n = 0; n < 24; n++) db.Add(new Post { Platform = n < 20 ? "facebook" : "instagram", PlatformPostId = "synthetic-" + n,
                AuthorId = n < 20 ? author.Id : null, PostedAt = n == 0 ? null : Clock.Now, Hidden = n % 3 == 0,
                CategoriesJson = n % 2 == 0 ? "[\"topic\"]" : null, VerdictContentRevision = n % 4 == 0 ? 1 : null,
                Text = new string('x', 20000) });
            await db.SaveChangesAsync();
        }
        var snapshot = Snapshot with { Config = Snapshot.Config with { Ui = new() { RenderCap = 3, Stack = new() { MinPosts = 0 } } },
            Taxonomy = new() { Categories = [new() { Key = "topic", Label = "Topic" }], Views = [new() { Key = "topic", Category = "topic" }, new() { Key = "people", Authors = ["fb:fixture"] }, new() { Key = "union", Union = ["topic", "people"] }] } };
        await using var check = i.Factory.Open(); var posts = await check.Posts.AsNoTracking().ToArrayAsync();
        var query = new FeedQuery(i.Factory);
        foreach (var model in new[] { false, true }) foreach (var platform in new string?[] { null, "facebook", "instagram" })
        foreach (var view in new[] { "all", "topic", "people", "union" }) foreach (var scope in new[] { "live", "hidden", "unsorted" })
        {
            var s = snapshot with { Config = snapshot.Config with { Llm = new() { Enabled = model } } };
            var scoped = posts.Where(p => p.Platform == (platform ?? "facebook")).ToArray();
            bool Match(Post p) => view switch { "all" => !model || p.Judged, "topic" => p.Judged, "people" => p.AuthorId != null, _ => p.Judged || p.AuthorId != null };
            var expected = scoped.Where(p => scope == "hidden" ? p.Hidden : scope == "unsorted" ? !p.Hidden && !p.Judged : !p.Hidden && Match(p)).OrderByDescending(p => p.PostedAt).ThenByDescending(p => p.Id).Take(3).Select(p => p.Id);
            var result = await query.Read(s, view, platform, scope, default);
            Assert.Equal(expected, result.Items.Select(u => u.Lead.Post.Id));
            Assert.Equal(scoped.Count(p => !p.Hidden && Match(p)), result.VisibleCount);
            Assert.Equal(scoped.Count(p => p.Hidden), result.HiddenCount);
            Assert.Equal(scoped.Count(p => !p.Hidden && !p.Judged), result.UnsortedCount);
            Assert.Equal(posts.Count(p => p.Hidden), result.TotalHidden);
            Assert.Equal(posts.Count(p => !p.Hidden && !p.Judged), result.TotalUnsorted);
        }
    }

    [Fact] public async Task RecoveryIndicatorMatchesDetailedQueryForPendingAndCompletedWork()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        var p = new Post { Platform = "facebook", PlatformPostId = "synthetic", IngestReadyAt = Clock.Now };
        db.Add(p); await db.SaveChangesAsync();
        foreach (var hidden in new[] { false, true }) foreach (var owner in new[] { "llm", "audience" })
        foreach (var current in new[] { false, true }) foreach (var error in new string?[] { null, "synthetic failure" })
        foreach (var age in new[] { 0, -3600 }) foreach (var task in new[] { "judge", "summary" })
        {
            p.Hidden = hidden; p.HiddenBy = owner; p.CategoriesJson = "[]"; p.VerdictContentRevision = current ? 1 : null;
            p.Summary = "synthetic"; p.SummaryContentRevision = current ? 1 : null;
            p.LlmAttemptedAt = task == "judge" ? Clock.Now.AddSeconds(age) : null; p.LlmError = error;
            p.SummaryAttemptedAt = task == "summary" ? Clock.Now.AddSeconds(age) : null; p.SummaryError = error;
            await db.SaveChangesAsync();
            Assert.Equal((await RecoveryQuery.Read(db, Snapshot)).Length > 0, await RecoveryQuery.HasIssues(db, Snapshot));
            Assert.False(await RecoveryQuery.HasIssues(db, Snapshot, "instagram"));
        }
    }

    [Fact] public async Task CoverageIndicatorMatchesLegacyUrlHistoryAndLatestIncompleteVisit()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        var author = new Author { Platform = "facebook", IsFriend = true, Url = "https://example.test/person/" }; db.Add(author); await db.SaveChangesAsync();
        async Task Check() => Assert.Equal((await CoverageQuery.Read(db, Snapshot)).Any(c => c.Stale || c.Incomplete), await CoverageQuery.HasIssues(db, Snapshot));
        await Check();
        var visit = new TimelineVisit { Platform = "facebook", Url = "https://example.test/person", CaptureStatus = "captured", At = Clock.Now.AddDays(-4) };
        db.Add(visit); await db.SaveChangesAsync(); await Check();
        visit.At = Clock.Now.AddMinutes(-1); await db.SaveChangesAsync(); await Check(); Assert.False(await CoverageQuery.HasIssues(db, Snapshot));
        db.Add(new TimelineVisit { Platform = "facebook", AuthorId = author.Id, At = Clock.Now, CaptureStatus = "incomplete" });
        await db.SaveChangesAsync(); await Check(); Assert.True(await CoverageQuery.HasIssues(db, Snapshot));
        Assert.False(await CoverageQuery.HasIssues(db, Snapshot, "instagram"));
    }
}
