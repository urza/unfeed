using System.Collections.Immutable;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Feed.Web;
using Xunit;

namespace Feed.Tests;
public sealed class RecoveryPresentationTests
{
    [Fact] public void PartialMetadataNoticeExplainsMissingLinksAndStoredPostProtection()
    {
        var issue = new RecoveryIssue("instagram", "saved capture", "partial metadata unavailable", 0, null, null, "Recorded warning", true);
        Assert.Equal("partial", RecoveryPresentation.Category(issue));
        Assert.Equal("Some post metadata was unavailable", RecoveryPresentation.Title("partial"));
        Assert.Contains("missing fields may include links", RecoveryPresentation.Explanation("partial"));
        Assert.Contains("Existing stored posts were protected", RecoveryPresentation.Explanation("partial"));
    }
    [Fact] public async Task RetryPauseExhaustionAndNoticesRemainDistinctAndCaptureAssociationIsExplicit()
    {
        await using var instance = new TestInstance(); await instance.Init();
        await using var db = instance.Factory.Open();
        var run = new Run { Platform = "instagram", Kind = "collect", Status = "ok" }; db.Add(run); await db.SaveChangesAsync();
        db.Add(new RawSnapshot { Platform = "instagram", RunId = run.Id, Path = "raw/synthetic.json", Parsed = true, Warning = "location/profile_pic_url unavailable" });
        var post = new Post { Platform = "instagram", PlatformPostId = "synthetic", LatestRawRef = "raw/synthetic.json", IngestReadyAt = Clock.Now, LlmAttemptedAt = Clock.Now.AddMinutes(-40), LlmFailures = 1, LlmTokenLimit = 4000, LlmError = "model token budget exhausted (max_tokens=4000)" }; db.Add(post); await db.SaveChangesAsync();
        var config = new FeedConfig { Scheduler = new() { Enabled = true }, Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("instagram", new() { Enabled = true }), Llm = new() { Enabled = true, MaxTokens = 4000, TokenRetryBudgets = [8000] } };
        var snapshot = new InstanceSnapshot(config, new(), Preferences.Parse(""));
        var rows = await RecoveryQuery.Read(db, snapshot);
        var retry = Assert.Single(rows, r => !r.Informational);
        Assert.Equal(RecoveryDisposition.Automatic, retry.Disposition); Assert.Equal(8000, retry.NextTokenBudget); Assert.Equal(run.Id, retry.RunId); Assert.Equal(post.Id, retry.PostId);
        Assert.Equal("tokens", RecoveryPresentation.Category(retry)); Assert.Contains("does not mean an attempt is running", RecoveryPresentation.Timing(retry, Clock.Now));
        Assert.Equal(RecoveryDisposition.Informational, Assert.Single(rows, r => r.Informational).Disposition);
        var paused = await RecoveryQuery.Read(db, snapshot with { Config = config with { Scheduler = new() { Enabled = false } } });
        Assert.Equal(RecoveryDisposition.Paused, Assert.Single(paused, r => !r.Informational).Disposition);
        post.LlmTokenLimit = 8000; await db.SaveChangesAsync();
        Assert.Equal(RecoveryDisposition.Attention, Assert.Single(await RecoveryQuery.Read(db, snapshot), r => !r.Informational).Disposition);
        post.LlmTokenLimit = null; post.LlmError = null; post.CategoriesJson = "[]"; post.VerdictContentRevision = post.ContentRevision; await db.SaveChangesAsync();
        Assert.All(await RecoveryQuery.Read(db, snapshot), r => Assert.True(r.Informational));
    }
    [Fact] public async Task SelectedOlderCollectionKeepsItsOwnVisitEvidenceAndOnlyCurrentSuccessesAreListed()
    {
        await using var instance = new TestInstance(); await instance.Init();
        await using var db = instance.Factory.Open();
        var old = new Run { Kind = "collect", Platform = "facebook", Status = "ok" }; db.Add(old); await db.SaveChangesAsync();
        db.Add(new TimelineVisit { RunId = old.Id, Platform = "facebook", Name = "Synthetic profile", CaptureStatus = "incomplete", Note = "Synthetic gap" });
        for(var n = 0; n < 11; n++) db.Add(new Run { Kind = "collect", Platform = "facebook", Status = "ok" });
        db.Add(new Post { Platform = "facebook", PlatformPostId = "completed", JudgedAt = Clock.Now, CategoriesJson = "[]", VerdictContentRevision = 1 });
        db.Add(new Post { Platform = "facebook", PlatformPostId = "stale", JudgedAt = Clock.Now, CategoriesJson = "[]", VerdictContentRevision = 1, ContentRevision = 2 });
        await db.SaveChangesAsync();
        var management = new Management(instance.Paths, instance.Factory, new ManagementFiles(instance.Paths));
        var page = await management.Read("recovery", "", "", "", 1, default, old.Id);
        Assert.Equal(11, page.Collections.Length); Assert.Contains(page.Collections, r => r.Id == old.Id);
        Assert.Equal("Synthetic gap", Assert.Single(page.CollectionVisits).Note);
        Assert.Equal("Finished with coverage gaps", RecoveryPresentation.RunStatus(old, page.CollectionVisits));
        Assert.Single(page.RecentCompletions);
    }
    [Fact] public void FutureRetryAndUnknownCaptureNeverClaimWorkIsRunning()
    {
        var now = Clock.Now;
        var issue = new RecoveryIssue("facebook", "post #1 · summary", "timeout", 1, now, now.AddMinutes(20), "Automatic retry after delay") { Disposition = RecoveryDisposition.Automatic };
        Assert.Contains("in 20 minutes", RecoveryPresentation.Timing(issue, now)); Assert.Null(issue.RunId);
        Assert.Equal("summary", RecoveryPresentation.Category(issue));
    }
}
