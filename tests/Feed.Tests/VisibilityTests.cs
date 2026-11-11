using System.Diagnostics;
using System.Reflection;
using Feed.Cli;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace Feed.Tests;

public sealed class VisibilityTests
{
    const string Mute = "## Muted people\n- mute: fb:101\n", Show = "## Always show\n- show: fb:101\n", Keyword = "## Never show — my rules\n- keyword: synthetic\n";
    static readonly Author Friend = new() { Id = 1, Platform = "facebook", DisplayName = "Synthetic One", IsFriend = true, RefsJson = "[\"fb:101\"]" };
    static InstanceSnapshot Snapshot(string preferences = "", int threshold = 4, FilterConfig? filters = null) => new(new() { Llm = new() { Threshold = threshold }, Filters = filters ?? new() }, new(), Preferences.Parse(preferences));
    static RuleContext Rules(InstanceSnapshot s, Author? author = null) => new(author ?? Friend, [Friend], s);
    // A post with a verdict for its current content.
    static Post Judged(int score, string? text = null) => new() { Platform = "facebook", PlatformPostId = Guid.NewGuid().ToString("N"), AuthorId = Friend.Id, Text = text, CategoriesJson = "[]", VerdictContentRevision = 1, LlmScore = score, LlmReason = "synthetic" };
    static Post Hidden(Post p, string owner, string reason = "stored") { p.Hidden = true; p.HiddenBy = owner; p.HiddenReason = reason; return p; }

    [Fact]
    public void DecideGivesEachOwnerInGateOrder()
    {
        Assert.Null(Visibility.Decide(new Post { Platform = "facebook" }, Rules(Snapshot())));
        Assert.Equal(("structural", "sponsored"), Visibility.Decide(new Post { Platform = "facebook", IsSponsored = true }, Rules(Snapshot())));
        Assert.Equal(("whitelist", "outside configured audience"), Visibility.Decide(new Post { Platform = "facebook" }, new RuleContext(null, [Friend], Snapshot())));
        Assert.Equal(("keyword", "keyword: synthetic"), Visibility.Decide(new Post { Platform = "facebook", Text = "A Synthetic text" }, Rules(Snapshot(Keyword))));
        Assert.Equal(("mute", "muted: fb:101"), Visibility.Decide(new Post { Platform = "facebook" }, Rules(Snapshot(Mute))));
        Assert.Equal(("llm", "llm 3: synthetic"), Visibility.Decide(Judged(3), Rules(Snapshot())));
        // The threshold is exclusive: a score equal to it stays visible.
        Assert.Null(Visibility.Decide(Judged(4), Rules(Snapshot())));
        // A deterministic gate owns the hide before the model does.
        Assert.Equal("mute", Visibility.Decide(Judged(3), Rules(Snapshot(Mute)))!.Value.Owner);
    }
    [Fact]
    public void AlwaysShowShieldsOnlyTheConfiguredGates()
    {
        var post = Judged(3, "synthetic");
        Assert.Null(Visibility.Decide(post, Rules(Snapshot(Show + Keyword))));
        Assert.Equal("llm", Visibility.Decide(post, Rules(Snapshot(Show + Keyword, filters: new() { AlwaysShowBypasses = ["keyword"] })))!.Value.Owner);
        Assert.Equal("keyword", Visibility.Decide(post, Rules(Snapshot(Show + Keyword, filters: new() { AlwaysShowBypasses = [] })))!.Value.Owner);
    }
    [Fact]
    public void ReservedOwnersStayAsStored()
    {
        var other = Hidden(new Post { Platform = "facebook" }, "other");
        Assert.Equal(("other", "stored"), Visibility.Decide(other, Rules(Snapshot())));
        Filters.Apply(other, Rules(Snapshot(Mute))); Assert.Equal("other", other.HiddenBy); Assert.Equal(0, other.VisibilityRevision);
        // SetHidden rejects "thumbs", so Apply must not try to write this row.
        var thumbs = Hidden(new Post { Platform = "facebook" }, "thumbs");
        Filters.Apply(thumbs, Rules(Snapshot())); Assert.True(thumbs.Hidden); Assert.Equal(0, thumbs.VisibilityRevision);
    }
    [Fact]
    public void ModelHidesOnlyAVerdictForTheCurrentContent()
    {
        // Step 2, first change: an `llm` hide survived a content change at ingest before. Fail-open now shows the post until the next verdict.
        var changed = Hidden(Judged(1), "llm"); changed.ContentRevision++;
        Filters.Apply(changed, Rules(Snapshot())); Assert.False(changed.Hidden);
        var judgedAgain = Judged(1); judgedAgain.VerdictContentRevision = judgedAgain.ContentRevision = 2;
        Filters.Apply(judgedAgain, Rules(Snapshot())); Assert.Equal("llm", judgedAgain.HiddenBy);
    }
    [Fact]
    public void ApplyRestoresTheModelHideWhenAGateClears()
    {
        // Step 2, second change: ingest and identity merge call Filters.Apply. Before, only Manage re-applied the low score.
        var post = Hidden(Judged(2), "mute");
        Filters.Apply(post, Rules(Snapshot())); Assert.True(post.Hidden); Assert.Equal("llm", post.HiddenBy); Assert.Equal("llm 2: synthetic", post.HiddenReason);
    }
    [Fact]
    public void ApplyClearsAModelHideAtOrAboveTheThresholdAndWritesOnlyOnChange()
    {
        // Step 2, third change: before, only a fresh verdict cleared an `llm` hide.
        var post = Hidden(Judged(5), "llm", "llm 5: synthetic");
        Filters.Apply(post, Rules(Snapshot(threshold: 6))); Assert.True(post.Hidden); Assert.Equal(0, post.VisibilityRevision);
        Filters.Apply(post, Rules(Snapshot(threshold: 5))); Assert.False(post.Hidden); Assert.Equal(1, post.VisibilityRevision);
        Filters.Apply(post, Rules(Snapshot(threshold: 5))); Assert.Equal(1, post.VisibilityRevision);
    }
    [Fact]
    public async Task OwnerUnhideHoldsAgainstTheModelUntilTheContentChanges()
    {
        await using var i = new TestInstance(); await i.Init(); long id; var friend = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" };
        await using (var db = i.Factory.Open()) { db.Add(friend); await db.SaveChangesAsync(); var p = Judged(1); p.AuthorId = friend.Id; p.SetHidden("llm", "llm 1: synthetic"); db.Add(p); await db.SaveChangesAsync(); id = p.Id; }
        async Task<Post> Reapplied(InstanceSnapshot s, Action<Post>? change = null)
        {
            await using var db = i.Factory.Open(); if (change is not null) { change(await db.Posts.SingleAsync()); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); }
            await VisibilityStore.Reapply(db, db.Posts, s, default); return await db.Posts.AsNoTracking().SingleAsync();
        }
        Assert.True(await new Actions(i.Factory).Unhide(id));
        var post = await Reapplied(Snapshot()); Assert.False(post.Hidden); Assert.Equal(post.ContentRevision, post.OwnerUnhiddenRevision);
        // A deterministic gate still wins over the owner's unhide.
        Assert.Equal("mute", (await Reapplied(Snapshot(Mute))).HiddenBy);
        Assert.False((await Reapplied(Snapshot())).Hidden);
        // New content with a new low verdict: the unhide covered the old content only.
        post = await Reapplied(Snapshot(), p => { p.ContentRevision++; p.VerdictContentRevision = p.ContentRevision; });
        Assert.Equal("llm", post.HiddenBy);
    }
    [Fact]
    public async Task FreshVerdictDoesNotUndoAnOwnerUnhideOfAMutedPost()
    {
        // The owner unhid a muted author's unjudged post. The next run judges it; the verdict must not bring the mute back.
        await using var i = new TestInstance(); await i.Init(); long id; var friend = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" };
        await using (var db = i.Factory.Open()) { db.Add(friend); await db.SaveChangesAsync(); var p = new Post { Platform = "facebook", PlatformPostId = "muted", AuthorId = friend.Id, Text = "synthetic", IngestReadyAt = Clock.Now }; p.SetHidden("mute", "muted: fb:101"); db.Add(p); await db.SaveChangesAsync(); id = p.Id; }
        Assert.True(await new Actions(i.Factory).Unhide(id));
        var s = new InstanceSnapshot(new() { Platforms = System.Collections.Immutable.ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()), Llm = new() { Enabled = true, BaseUrl = "http://synthetic.test/v1", FallbackBaseUrl = "", Vision = false } }, new(), Preferences.Parse(Mute));
        using var handler = new StubHandler(_ => Replies.Llm("{\"score\":9,\"reason\":\"fine\",\"categories\":[]}")); using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http);
        var counts = await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(s, new("rescore"), null, default);
        Assert.Equal(1, counts.Completed);
        await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync(); Assert.Equal(9, post.LlmScore); Assert.False(post.Hidden);
    }
    [Fact]
    public async Task ReapplyCountsChangesAndAdvancesEveryRevisionInScope()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open())
        {
            var friend = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(friend); await db.SaveChangesAsync();
            var low = Judged(1); var stale = Hidden(Judged(9), "llm"); var same = Judged(9); var outside = Judged(1); outside.Platform = "instagram";
            foreach (var p in new[] { low, stale, same, outside }) p.AuthorId = friend.Id;
            db.AddRange(low, stale, same, outside); await db.SaveChangesAsync();
        }
        await using var work = i.Factory.Open();
        var counts = await VisibilityStore.Reapply(work, work.Posts.Where(p => p.Platform == "facebook"), Snapshot(), default);
        Assert.Equal(new VisibilityStore.Counts(3, 1, 1), counts);
        var posts = await work.Posts.AsNoTracking().ToArrayAsync();
        // The unchanged post advances too: a verdict in flight for it must count as superseded.
        Assert.All(posts.Where(p => p.Platform == "facebook"), p => Assert.True(p.VisibilityRevision >= 1));
        var untouched = posts.Single(p => p.Platform == "instagram"); Assert.False(untouched.Hidden); Assert.Equal(0, untouched.VisibilityRevision);
    }
    [Fact]
    public async Task CliRefilterRestoresTheModelHideAfterAnUnmute()
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}}}""");
        await using (var db = i.Factory.Open())
        {
            var friend = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(friend); await db.SaveChangesAsync();
            // The state after an unmute: the mute entry is gone from preferences, the stored hide is still `mute`.
            var p = Judged(2); p.AuthorId = friend.Id; p.SetHidden("mute", "muted: fb:101"); db.Add(p); await db.SaveChangesAsync();
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        var configuration = typeof(CliProgram).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root!.FullName, "src", "Feed.Cli", "bin", configuration, "net10.0", "Feed.Cli.dll"), "refilter", "--data", i.Paths.Root, "--all" }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var text = await output + await error;
            Assert.True(child.ExitCode == 0 && text.Contains("refilter: total=1, hidden=0, shown=0"), text);
            await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync();
            Assert.True(post.Hidden); Assert.Equal("llm", post.HiddenBy);
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
    [Fact]
    public void VersionChangesOnlyWithWhatTheModelSees()
    {
        var s = Snapshot();
        InstanceSnapshot With(FilterConfig f) => s with { Config = s.Config with { Filters = f } };
        Assert.Equal(Prompts.Version(s), Prompts.Version(Snapshot(threshold: 7)));
        Assert.Equal(Prompts.Version(With(new() { AlwaysShowBypasses = ["keyword", "llm"] })), Prompts.Version(With(new() { AlwaysShowBypasses = ["llm", "types"] })));
        // Policy adds the always-show sentence to every prompt only under the `llm` bypass.
        Assert.NotEqual(Prompts.Version(With(new() { AlwaysShowBypasses = ["keyword", "llm"] })), Prompts.Version(With(new() { AlwaysShowBypasses = ["keyword"] })));
        // The model sees author_close_friend, so a close-friend edit must mark verdicts as stale.
        var friends = s with { Config = s.Config with { Platforms = s.Config.Platforms.SetItem("facebook", new() { CloseFriends = ["fb:101"] }) } };
        Assert.NotEqual(Prompts.Version(s), Prompts.Version(friends));
    }
    [Fact]
    public async Task RestampMovesCurrentVerdictsToTheNewVersionOnce()
    {
        await using var i = new TestInstance(); await i.Init(); var s = Snapshot();
        var legacyHash = typeof(Prompts).GetMethod("LegacyConfigurationHash", BindingFlags.NonPublic | BindingFlags.Static)!;
        string Legacy(bool textOnly) { var current = Prompts.Version(s, textOnly); return current[..(current.LastIndexOf(".m") + 2)] + (string)legacyHash.Invoke(null, [s, textOnly])!; }
        Assert.NotEqual(Legacy(false), Prompts.Version(s));
        await using var db = i.Factory.Open();
        Post Stamped(string version) { var p = Judged(9); p.AuthorId = null; p.PrefsVersion = version; return p; }
        Post vision = Stamped(Legacy(false)), text = Stamped(Legacy(true)), stale = Stamped("older"); db.AddRange(vision, text, stale); await db.SaveChangesAsync();
        await Prompts.RestampVersions(db, s); db.ChangeTracker.Clear();
        Assert.Equal(Prompts.Version(s), (await db.Posts.FindAsync(vision.Id))!.PrefsVersion);
        Assert.Equal(Prompts.Version(s, true), (await db.Posts.FindAsync(text.Id))!.PrefsVersion);
        Assert.Equal("older", (await db.Posts.FindAsync(stale.Id))!.PrefsVersion);
        // The Kv marker stops a second pass.
        var late = Stamped(Legacy(false)); db.Add(late); await db.SaveChangesAsync();
        await Prompts.RestampVersions(db, s); db.ChangeTracker.Clear();
        Assert.Equal(Legacy(false), (await db.Posts.FindAsync(late.Id))!.PrefsVersion);
    }

    static FormCollection ProcessingForm(string version, int threshold, int parallel = 1) => new(new (string Key, string Value)[] { ("version", version), ("enabled", "false"), ("vision", "false"), ("summary_languages", ""), ("threshold", threshold.ToString()), ("parallel", parallel.ToString()), ("timeout_seconds", "60"), ("max_tokens", "1000"), ("summary_max_tokens", "1000") }.ToDictionary(x => x.Key, x => new StringValues(x.Value)));
    static async Task<(TestInstance Instance, Management Management, ManagementFiles Files)> ThresholdInstance()
    {
        var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}},"filters":{"audience":"all_captured"},"llm":{"threshold":4}}""");
        await using (var db = i.Factory.Open()) { var p = Judged(5); p.AuthorId = null; db.Add(p); await db.SaveChangesAsync(); }
        var files = new ManagementFiles(i.Paths); return (i, new Management(i.Paths, i.Factory, files), files);
    }
    [Fact]
    public async Task ThresholdSaveReappliesStoredVerdictsWithoutTheModel()
    {
        var (i, management, files) = await ThresholdInstance(); await using var instance = i;
        // Management holds no model client, so the reapply cannot make a model request.
        Assert.Equal("Saved. Threshold reapplied: 1 hidden, 0 shown, no model calls.", await management.SaveSettings("processing", ProcessingForm(files.Read().Version, 6), default));
        await using (var check = i.Factory.Open()) { Assert.Equal("llm", (await check.Posts.SingleAsync()).HiddenBy); Assert.Null(await check.Get("management:refilter_pending")); }
        Assert.Equal("Saved. Threshold reapplied: 0 hidden, 1 shown, no model calls.", await management.SaveSettings("processing", ProcessingForm(files.Read().Version, 4), default));
        await using var after = i.Factory.Open(); var post = await after.Posts.SingleAsync(); Assert.False(post.Hidden); int revision = post.VisibilityRevision;
        // An unchanged threshold leaves the stored posts alone.
        Assert.StartsWith("Saved. New runs use these settings", await management.SaveSettings("processing", ProcessingForm(files.Read().Version, 4, parallel: 2), default));
        await after.Entry(post).ReloadAsync(); Assert.Equal(revision, post.VisibilityRevision); Assert.Null(await after.Get("management:refilter_pending"));
    }
    [Fact]
    public async Task ThresholdSaveSucceedsDuringARunAndReappliesWhenTheLockIsFree()
    {
        var (i, management, files) = await ThresholdInstance(); await using var instance = i;
        using (ResourceLock.Try(i.Paths, "processing"))
        {
            Assert.StartsWith("Saved. Existing posts follow the new threshold when the active run ends", await management.SaveSettings("processing", ProcessingForm(files.Read().Version, 6), default));
            Assert.Equal(6, files.Read().Snapshot().Config.Llm.Threshold);
            await using var during = i.Factory.Open(); Assert.False((await during.Posts.SingleAsync()).Hidden); Assert.Equal("1", await during.Get("management:refilter_pending"));
            await management.ResumeRefilter(default); Assert.False((await during.Posts.AsNoTracking().SingleAsync()).Hidden);
        }
        await management.ResumeRefilter(default);
        await using var check = i.Factory.Open(); Assert.Equal("llm", (await check.Posts.SingleAsync()).HiddenBy); Assert.Null(await check.Get("management:refilter_pending"));
    }
}
