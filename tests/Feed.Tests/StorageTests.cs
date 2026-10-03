using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class TestInstance : IAsyncDisposable
{
    public InstancePaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "feed-tests-" + Guid.NewGuid().ToString("N")));
    public DbFactory Factory { get; }
    public TestInstance() { Paths.Create(); Factory = new(Paths); }
    public async Task Init() { await using var db = Factory.Open(); await db.Initialize(); }
    public ValueTask DisposeAsync() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); Directory.Delete(Paths.Root, true); return ValueTask.CompletedTask; }
}
public sealed class StorageTests
{
    [Fact] public async Task MigrationAndRequestUniqueness() { await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open(); await Actions.EnsureRequest(db, "process", null); await Actions.EnsureRequest(db, "process", null); Assert.Single(await db.RunRequests.ToListAsync()); await i.Init(); }
    [Fact] public async Task IdentityMergesAliasesWithoutMergingUnknowns()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        Assert.Null(await IdentityStore.Resolve(db, "instagram", new(null, "Synthetic One", null), true, default));
        var a = await IdentityStore.Resolve(db, "instagram", new("123", "Synthetic One", null), true, default);
        var b = await IdentityStore.Resolve(db, "instagram", new(null, "Synthetic Two", "https://instagram.com/synthetic/"), false, default); Assert.NotEqual(a!.Id, b!.Id);
        var merged = await IdentityStore.Resolve(db, "instagram", new("123", "Synthetic One", "https://instagram.com/synthetic/"), false, default); Assert.Equal(a.Id, merged!.Id); Assert.True(merged.IsFriend); Assert.Single(await db.Authors.ToListAsync()); Assert.Equal(2, Identity.Keys(merged).Length);
    }
    [Fact] public async Task ReplayFiltersAtomicallyAndPreservesRevision()
    {
        await using var i = new TestInstance(); await i.Init(); var dir = i.Paths.Get("raw", "instagram", "20260101T000000"); Directory.CreateDirectory(dir); var file = Path.Combine(dir, "001.json");
        await File.WriteAllTextAsync(file, "{\"items\":[{\"pk\":\"101\",\"code\":\"synthetic\",\"user\":{\"pk\":\"501\",\"username\":\"fixture.person\"},\"caption\":{\"text\":\"Synthetic caption\"}}]}");
        var s = new InstanceFiles(i.Paths).Current; var ingest = new Ingest(i.Paths, i.Factory, new(i.Paths, new HttpClient()));
        Assert.Equal(1, (await ingest.File("instagram", file, s, true, default)).New);
        Assert.Equal(0, (await ingest.File("instagram", file, s, true, default)).Revised);
        await using var db = i.Factory.Open(); var post = await db.Posts.SingleAsync(); Assert.True(post.Hidden); Assert.Equal("whitelist", post.HiddenBy); Assert.Equal(1, post.ContentRevision); Assert.NotNull(post.IngestReadyAt);
        post.Summary = "old"; post.SummaryContentRevision = 1; await db.SaveChangesAsync();
        await File.WriteAllTextAsync(file, (await File.ReadAllTextAsync(file)).Replace("Synthetic caption", "Changed caption")); Assert.Equal(1, (await ingest.File("instagram", file, s, true, default)).Revised);
        await db.Entry(post).ReloadAsync(); Assert.Equal(2, post.ContentRevision); Assert.Equal("old", post.Summary); Assert.NotEqual(post.ContentRevision, post.SummaryContentRevision);
    }
    [Fact] public async Task StoryContextRoundTripsAndInvalidatesVerdictOnlyWhenChanged()
    {
        await using var i = new TestInstance(); await i.Init();
        var dir = i.Paths.Get("raw", "facebook", "synthetic"); Directory.CreateDirectory(dir); var file = Path.Combine(dir, "001.json");
        const string raw = """{"__typename":"Story","post_id":"synthetic","creation_time":1700000000,"actors":[{"id":"501","name":"Synthetic"}],"message":{"text":"Caption"},"comet_sections":{"context_layout":{"story":{"comet_sections":{"title":{"story":{"title":{"text":"Synthetic updated their cover photo."}}}}}}}}""";
        await File.WriteAllTextAsync(file, raw); var snapshot = new InstanceFiles(i.Paths).Current;
        var ingest = new Ingest(i.Paths, i.Factory, new(i.Paths, new HttpClient())); await ingest.File("facebook", file, snapshot, true, default);
        await using var db = i.Factory.Open(); var post = await db.Posts.SingleAsync();
        Assert.Equal("Synthetic updated their cover photo.", post.StoryTitle); Assert.Equal("Caption", post.Text);
        post.CategoriesJson = "[]"; post.VerdictContentRevision = post.ContentRevision; post.LlmTokenLimit = 8000; await db.SaveChangesAsync();
        Assert.Equal(0, (await ingest.File("facebook", file, snapshot, true, default)).Revised);
        await File.WriteAllTextAsync(file, raw.Replace("cover photo", "profile picture"));
        Assert.Equal(1, (await ingest.File("facebook", file, snapshot, true, default)).Revised);
        await db.Entry(post).ReloadAsync(); Assert.False(post.Judged); Assert.Null(post.LlmTokenLimit); Assert.Equal("Synthetic updated their profile picture.", post.StoryTitle);
    }
    [Fact] public void CarouselChildrenAreExcludedEverywhere()
    {
        const string slide = "{\"pk\":\"2\",\"code\":\"child\",\"media_type\":1,\"display_url\":\"https://example.test/slide.jpg\"}";
        var result = PayloadParser.Parse("instagram", "{\"items\":[{\"pk\":\"1\",\"code\":\"parent\",\"user\":{\"username\":\"synthetic\"},\"carousel_media\":[" + slide + "]}," + slide + "]}");
        var parent = Assert.Single(result.Posts); Assert.Equal("1", parent.Post.PlatformPostId); Assert.Single(parent.Media);
    }
    [Fact] public void CompletenessRequiresUnbrokenTerminalChain()
    {
        CompletionEvidence[] pages = [new("owner:following", null, "a", false, 80), new("owner:following", "a", null, true, 80)];
        Assert.True(FriendsCompleteness.Proven(pages, 80, false, out _)); Assert.False(FriendsCompleteness.Proven(pages[..1], 80, false, out _)); Assert.False(FriendsCompleteness.Proven(pages, 79, false, out _)); Assert.False(FriendsCompleteness.Proven(pages, 80, true, out _));
    }
    [Theory] [InlineData("{\"score\":true,\"reason\":\"x\"}")] [InlineData("{\"score\":7.5,\"reason\":\"x\"}")] [InlineData("{\"score\":11,\"reason\":\"x\"}")] public void VerdictRejectsInvalidScores(string reply) => Assert.ThrowsAny<Exception>(() => Prompts.ParseVerdict(reply, new()));
    [Fact] public void VerdictAllowsNoCategoryButStillRejectsMissingOrInvalidLabels()
    {
        var taxonomy = new Taxonomy { Categories = [new() { Key = "topic", Label = "Topic" }] };
        var verdict = Prompts.ParseVerdict("{\"score\":7,\"reason\":\"No matching category\",\"categories\":[]}", taxonomy);
        Assert.Empty(verdict.Categories); Assert.Equal(7, verdict.Score);
        foreach (var suffix in new[] { "", ",\"categories\":null", ",\"categories\":\"topic\"", ",\"categories\":[\"unknown\"]", ",\"categories\":[3]" })
            Assert.ThrowsAny<Exception>(() => Prompts.ParseVerdict("{\"score\":7,\"reason\":\"fixture\"" + suffix + "}", taxonomy));
        Assert.Contains("Do not lower the score", Prompts.ReplyShape(taxonomy));
        Assert.DoesNotContain("If nothing else fits", Prompts.ReplyShape(taxonomy));
    }
    [Fact] public void VerdictAcceptsFirstObjectAndEmptyTaxonomy() { var v = Prompts.ParseVerdict("prefix {\"score\":9,\"reason\":\"fine\"} trailing {bad}", new()); Assert.Equal(9, v.Score); Assert.Empty(v.Categories); }

    [Fact] public async Task MergeInvalidatesMovedPostContentAndPreservesFeedbackAndCoverage()
    {
        await using var i=new TestInstance();await i.Init();await using var db=i.Factory.Open();
        var a=await IdentityStore.Resolve(db,"facebook",new("123","Fixture one",null),true,default);
        var b=await IdentityStore.Resolve(db,"facebook",new(null,"Fixture two","https://facebook.com/fixture"),false,default);
        var p=new Post{Platform="facebook",PlatformPostId="post",AuthorId=b!.Id,CategoriesJson="[]",VerdictContentRevision=1,IngestReadyAt=Clock.Now};db.Add(p);await db.SaveChangesAsync();db.Feedback.Add(new(){PostId=p.Id,AuthorId=b.Id,Value=1});db.TimelineVisits.Add(new(){AuthorId=b.Id,Platform="facebook"});db.RunRequests.Add(new(){Platform="facebook",Mode="home",PersonAuthorId=b.Id,Person="https://facebook.com/fixture"});await db.SaveChangesAsync();
        await IdentityStore.Resolve(db,"facebook",new("123","Fixture merged","https://facebook.com/fixture"),false,default);
        await db.Entry(p).ReloadAsync();Assert.Equal(a!.Id,p.AuthorId);Assert.Equal(2,p.ContentRevision);Assert.False(p.Judged);Assert.Equal(a.Id,(await db.Feedback.AsNoTracking().SingleAsync()).AuthorId);Assert.Equal(a.Id,(await db.TimelineVisits.AsNoTracking().SingleAsync()).AuthorId);Assert.Equal(a.Id,(await db.RunRequests.AsNoTracking().SingleAsync()).PersonAuthorId);Assert.NotEmpty(p.ContentHash);
    }
}
