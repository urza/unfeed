using System.Collections.Immutable;
using System.Globalization;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Feed.Web.Components;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
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

    // A parser-blocked response is the owner's concern only when posts can be missing. Beside captured posts it is
    // parser backlog: it stays on the recovery page and lights no dot. The visit row is the evidence; a home page
    // has no visit row, so its run stands in. A visit without posts stays a risk even when the run found some.
    [Theory]
    [InlineData("unknown_or_content", "visit", 3, RecoveryDisposition.Backlog)]
    [InlineData("unknown_or_content", "visit", 0, RecoveryDisposition.Compatibility)]
    [InlineData("content", "visit", 3, RecoveryDisposition.Compatibility)]
    [InlineData("unknown_or_content", "run", 3, RecoveryDisposition.Backlog)]
    [InlineData("unknown_or_content", "run", 0, RecoveryDisposition.Compatibility)]
    public async Task BlockedResponseBesideCapturedPostsIsBacklog(string impact, string evidence, int posts, RecoveryDisposition expected)
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        db.Runs.Add(new Run { Id = 1, Kind = "collect", Platform = "facebook", Status = "ok", FinishedAt = Clock.Now, PostsFound = evidence == "run" ? posts : 50 });
        if (evidence == "visit") db.TimelineVisits.Add(new TimelineVisit { Platform = "facebook", Url = "https://www.facebook.com/10", RunId = 1, CaptureToken = "visit", Status = "rendered", PostsFound = posts });
        var raw = new RawSnapshot { Platform = "facebook", RunId = 1, Path = "blocked.json", CaptureToken = "visit", Error = "unknown", CapturedAt = Clock.Now, BlockedParserVersion = PayloadParser.Version };
        db.RawSnapshots.Add(raw); await db.SaveChangesAsync();
        db.Add(new RawDiagnostic { RawSnapshotId = raw.Id, Code = "unknown_response", Fingerprint = "shape-1", Impact = impact, ParserVersion = PayloadParser.Version }); await db.SaveChangesAsync();
        foreach (var rows in new[] { await RecoveryQuery.Read(db, Snapshot, grouped: true, summaryOnly: true), await RecoveryQuery.Read(db, Snapshot, fingerprint: "shape-1") })
            Assert.Equal(expected, Assert.Single(rows, r => r.RawId == raw.Id).Disposition);
        Assert.Equal(expected == RecoveryDisposition.Backlog ? DiagnosticLevel.None : DiagnosticLevel.Notice, await RecoveryQuery.Level(db, Snapshot));
    }
    // The dot must agree with the Manage page: an upstream warning the page files as informational cannot
    // light it, an unparsed capture the scheduler will retry is a notice, and a failed task the owner must
    // act on is an alert.
    [Fact] public async Task DotLevelFollowsRecoveryDispositions()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        db.Runs.Add(new Run { Id = 1, Kind = "collect", Platform = "facebook", Status = "ok", FinishedAt = Clock.Now });
        var warned = new RawSnapshot { Platform = "facebook", RunId = 1, Path = "warned.json", Parsed = true, Warning = "upstream", CapturedAt = Clock.Now };
        db.RawSnapshots.Add(warned); await db.SaveChangesAsync();
        db.Add(new RawDiagnostic { RawSnapshotId = warned.Id, Code = "upstream_error", Fingerprint = "f1", Impact = "auxiliary", ParserVersion = PayloadParser.Version }); await db.SaveChangesAsync();
        Assert.Equal(DiagnosticLevel.None, await RecoveryQuery.Level(db, Snapshot));
        db.RawSnapshots.Add(new RawSnapshot { Platform = "facebook", RunId = 1, Path = "failed.json", Error = "unknown", CapturedAt = Clock.Now, BlockedParserVersion = PayloadParser.Version }); await db.SaveChangesAsync();
        Assert.Equal(DiagnosticLevel.Notice, await RecoveryQuery.Level(db, Snapshot));
        var stale = new RawSnapshot { Platform = "facebook", RunId = 1, Path = "stale.json", Error = "unknown", CapturedAt = Clock.Now.AddDays(-RecoveryQuery.RawIssueDays - 1) };
        db.RawSnapshots.Add(stale); await db.SaveChangesAsync();
        Assert.DoesNotContain(await RecoveryQuery.Read(db, Snapshot), r => r.RawId == stale.Id);
        var post = new Post { Platform = "facebook", PlatformPostId = "exhausted", IngestReadyAt = Clock.Now, LlmAttemptedAt = Clock.Now.AddHours(-1), LlmError = "token limit", LlmTokenLimit = 1 << 20 };
        db.Add(post); await db.SaveChangesAsync();
        Assert.Equal(DiagnosticLevel.Alert, await RecoveryQuery.Level(db, Snapshot));
        Assert.Equal(DiagnosticLevel.None, await RecoveryQuery.Level(db, Snapshot, "instagram"));
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

    [Theory]
    [InlineData("2026-11-26 14:05:09")]
    [InlineData("2026-11-26T14:05:09")]
    [InlineData("2024-02-29 23:59:59.1")]
    [InlineData("2026-11-26 14:05:09.1234567")]
    [InlineData("2026-11-26T14:05:09Z")]
    [InlineData("2026-11-26 14:05:09.123+02:00")]
    public void SqliteDatesMatchesStandardDateTimeParse(string raw)
    {
        var expected = DateTime.Parse(raw, CultureInfo.InvariantCulture);
        var actual = SqliteDates.Parse(raw);
        Assert.Equal(expected.Ticks, actual.Ticks);
        Assert.Equal(expected.Kind, actual.Kind);
    }

    [Fact] public async Task FeedQueryTieBreaksEqualPostedAtAcrossRenderCapAndPlatforms()
    {
        await using var i = new TestInstance(); await i.Init();
        var newest = Clock.Now;
        var tie = newest.AddMinutes(-5);
        var older = newest.AddMinutes(-10);
        await using (var db = i.Factory.Open())
        {
            var fb = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:1\"]" };
            var ig = new Author { Platform = "instagram", IsFriend = true, RefsJson = "[\"ig:1\"]" };
            db.AddRange(fb, ig); await db.SaveChangesAsync();
            // Interleave 8 tied posts across both platforms so the cap=4 boundary splits the tie group.
            db.Posts.Add(new() { Platform = "facebook", PlatformPostId = "newest", AuthorId = fb.Id, PostedAt = newest, CategoriesJson = "[\"topic\"]", VerdictContentRevision = 1 });
            for (int n = 0; n < 8; n++)
            {
                bool isFb = n % 2 == 0;
                db.Posts.Add(new()
                {
                    Platform = isFb ? "facebook" : "instagram", PlatformPostId = $"tie-{n}",
                    AuthorId = isFb ? fb.Id : ig.Id, PostedAt = tie,
                    CategoriesJson = "[\"topic\"]", VerdictContentRevision = 1
                });
            }
            db.Posts.Add(new() { Platform = "instagram", PlatformPostId = "older", AuthorId = ig.Id, PostedAt = older, CategoriesJson = "[\"topic\"]", VerdictContentRevision = 1 });
            await db.SaveChangesAsync();
        }
        var snapshot = new InstanceSnapshot(
            new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()).Add("instagram", new()), Llm = new() { Enabled = true }, Ui = new() { RenderCap = 4, Stack = new() { MinPosts = 0 } } },
            new() { Categories = [new() { Key = "topic", Label = "Topic" }], Views = [new() { Key = "topic", Category = "topic" }, new() { Key = "people", Authors = ["fb:1", "ig:1"] }] },
            Preferences.Parse(""));
        await using var check = i.Factory.Open();
        var expected = (await check.Posts.AsNoTracking().ToArrayAsync())
            .OrderByDescending(p => p.PostedAt).ThenByDescending(p => p.Id).Take(4).Select(p => p.Id).ToArray();
        var query = new FeedQuery(i.Factory);
        foreach (var view in new[] { "all", "topic", "people" })
        {
            var page = await query.Read(snapshot, view, null, "live", default);
            Assert.Equal(10, page.VisibleCount);
            Assert.Equal(expected, page.Items.Select(u => u.Lead.Post.Id).ToArray());
        }
    }

    [Fact] public async Task CoverageQueryHandlesOutOfOrderVisitsAndFailureBarriers()
    {
        await using var i = new TestInstance(); await i.Init();
        var now = Clock.Now;
        await using var db = i.Factory.Open();
        var author = new Author { Platform = "facebook", IsFriend = true, DisplayName = "Fixture", Url = "https://example.test/person/" };
        db.Add(author); await db.SaveChangesAsync();
        // Insert out of chronological order across both AuthorId and legacy URL-only rows.
        db.TimelineVisits.AddRange(
            new() { Platform = "facebook", AuthorId = author.Id, Url = "https://example.test/person", At = now.AddMinutes(-2), CaptureStatus = "incomplete", Status = "unrendered", Note = "newer failure" },
            new() { Platform = "facebook", Url = "https://example.test/person", At = now.AddMinutes(-5), CaptureStatus = "captured", Status = "rendered", StopReason = "seen", OldestPostedAt = now.AddDays(-3), NewestPostedAt = now.AddHours(-1) },
            new() { Platform = "facebook", AuthorId = author.Id, Url = "https://example.test/person", At = now.AddMinutes(-1), CaptureStatus = "incomplete", Status = "unrendered", Note = "latest failure" },
            new() { Platform = "facebook", AuthorId = author.Id, Url = "https://example.test/person", At = now.AddMinutes(-10), CaptureStatus = "incomplete", Status = "unrendered", Note = "before barrier" },
            new() { Platform = "facebook", AuthorId = author.Id, Url = "https://example.test/person", At = now.AddDays(-65), CaptureStatus = "incomplete", Status = "unrendered", Note = "too old" });
        await db.SaveChangesAsync();
        var row = Assert.Single(await CoverageQuery.Read(db, Snapshot));
        Assert.Equal("latest failure", row.Note);
        Assert.Equal(2, row.Failures);
        Assert.True(row.Incomplete);
        Assert.False(row.Stale);
        Assert.Equal(now.AddDays(-3), row.OldestPost);
        Assert.Equal(now.AddHours(-1), row.NewestPost);
        Assert.True(await CoverageQuery.HasIssues(db, Snapshot));
    }

    [Fact] public async Task RecoveryReadWithSummaryMatchesSeparateReads()
    {
        await using var i = new TestInstance(); await i.Init();
        await using var db = i.Factory.Open();
        db.Runs.Add(new Run { Id = 1, Kind = "collect", Platform = "facebook", Status = "ok", FinishedAt = Clock.Now });
        var raw = new RawSnapshot { Platform = "facebook", RunId = 1, Path = "warned.json", Parsed = true, Warning = "upstream detail", CapturedAt = Clock.Now };
        db.RawSnapshots.Add(raw); await db.SaveChangesAsync();
        db.Add(new RawDiagnostic { RawSnapshotId = raw.Id, Code = "upstream_error", Fingerprint = "fp-1", Impact = "auxiliary", ParserVersion = PayloadParser.Version });
        db.Posts.Add(new Post { Platform = "facebook", PlatformPostId = "p1", IngestReadyAt = Clock.Now, LlmAttemptedAt = Clock.Now.AddHours(-1), LlmError = "timeout" });
        await db.SaveChangesAsync();
        var expectedSummary = await RecoveryQuery.Read(db, Snapshot, grouped: true, summaryOnly: true);
        var expectedPage = await RecoveryQuery.Read(db, Snapshot, grouped: true, page: 1, summaryOnly: false);
        var (actualSummary, actualPage) = await RecoveryQuery.ReadWithSummary(db, Snapshot, null, 1);
        Assert.Equal(expectedSummary, actualSummary);
        Assert.Equal(expectedPage, actualPage);
    }

    [Fact] public async Task HotQueriesUseCoveringAndPartialIndexes()
    {
        await using var i = new TestInstance(); await i.Init();
        await using var db = i.Factory.Open();
        await db.Database.OpenConnectionAsync();
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        async Task<string> Plan(string sql)
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
            var lines = new List<string>();
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) lines.Add(r.GetString(3));
            return string.Join("\n", lines);
        }
        Assert.Contains("COVERING INDEX IX_Posts_FeedMembership", await Plan("SELECT Id, PostedAt FROM Posts WHERE Platform = 'facebook' AND Hidden = 0 AND CategoriesJson IS NOT NULL AND VerdictContentRevision = ContentRevision ORDER BY PostedAt DESC"));
        Assert.Contains("COVERING INDEX IX_Posts_FeedMembership", await Plan("SELECT Id, PostedAt FROM Posts WHERE Platform = 'facebook' AND Hidden = 1 ORDER BY PostedAt DESC"));
        Assert.Contains("IX_Posts_JudgeQueue", await Plan("SELECT Id FROM Posts WHERE Platform = 'facebook' AND LlmAttemptedAt IS NOT NULL AND IngestReadyAt IS NOT NULL AND (Hidden = 0 OR HiddenBy = 'llm') AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision OR LlmTokenLimit IS NOT NULL)"));
        Assert.Contains("IX_Posts_SummaryQueue", await Plan("SELECT Id FROM Posts WHERE Platform = 'facebook' AND SummaryAttemptedAt IS NOT NULL AND IngestReadyAt IS NOT NULL AND Hidden = 0 AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)"));
        var rarePlan = await Plan("SELECT p.AuthorId, p.PostedAt, p.Id FROM Authors a JOIN Posts p ON p.AuthorId = a.Id WHERE a.Platform = 'facebook' AND a.IsFriend = 1 AND p.PostedAt IS NOT NULL ORDER BY a.Id, p.PostedAt");
        Assert.Contains("COVERING INDEX IX_Posts_AuthorHistory", rarePlan);
        Assert.DoesNotContain("TEMP B-TREE", rarePlan);
    }

    [Fact] public async Task RichTextLinksUrlsAndLongestUnicodeTagOnWordBoundaries()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, NullLoggerFactory.Instance);
        async Task<string> Render(string text, Tag[] tags) => await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<RichText>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Text"] = text, ["Tags"] = tags }));
            return output.ToHtmlString();
        });
        Tag[] tags = [new("Jan", "https://example.test/jan"), new("Jan Novák", "https://example.test/jan-novak"), new("Dvořák", "https://example.test/dvorak")];
        var html = await Render("Hi Jan Novák & Dvořáková and Dvořák at https://example.test/a?x=1&y=2", tags);
        Assert.Contains("<a class=\"tag\" href=\"https://example.test/jan-novak\"", html);
        Assert.Contains($"<strong>{System.Net.WebUtility.HtmlEncode("Jan Novák")}</strong>", html);
        Assert.DoesNotContain("https://example.test/jan\"", html);
        Assert.Contains($"&amp; {System.Net.WebUtility.HtmlEncode("Dvořáková")} and <a class=\"tag\" href=\"https://example.test/dvorak\"", html);
        Assert.Contains("<a href=\"https://example.test/a?x=1&amp;y=2\"", html);
        var plain = await Render("Just <b>plain</b> text", tags);
        Assert.Equal("<span class=\"rich-text\">Just &lt;b&gt;plain&lt;/b&gt; text</span>", plain);
    }
}
