using System.Collections.Immutable;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;

// Plan 09: rescore --trial judges a sample and writes nothing; the curate report marks disagreements.
public sealed class JudgeTrialTests
{
    static InstanceSnapshot Snapshot(int threshold = 4) => new(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()), Llm = new() { Enabled = true, BaseUrl = "http://synthetic.test/v1", FallbackBaseUrl = "", Vision = false, Threshold = threshold } }, new(), Preferences.Parse(""));
    static Post Judged(long authorId, int score, string labels = "[]") => new() { Platform = "facebook", PlatformPostId = Guid.NewGuid().ToString("N"), AuthorId = authorId, Text = "synthetic", IngestReadyAt = Clock.Now, PostedAt = Clock.Now, CategoriesJson = labels, VerdictContentRevision = 1, LlmScore = score, LlmReason = "stored" };
    sealed class Reply(string content) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Calls); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }), System.Text.Encoding.UTF8, "application/json") }); }
    }
    static async Task<(WorkCounts Counts, List<string> Lines)> Trial(TestInstance i, string verdict, int threshold = 4)
    {
        using var handler = new Reply(verdict); using var http = new HttpClient(handler); var media = new MediaFiles(i.Paths, http); var lines = new List<string>();
        var counts = await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(Snapshot(threshold), new("rescore", Trial: true), lines.Add, default);
        return (counts, lines);
    }
    static async Task<string> Rows(TestInstance i) { await using var db = i.Factory.Open(); return JsonSerializer.Serialize(await db.Posts.AsNoTracking().OrderBy(p => p.Id).ToListAsync()); }

    [Fact]
    public async Task TrialReportsWouldHideAndWritesNothing()
    {
        await using var i = new TestInstance(); await i.Init(); long visible, hidden, unjudged;
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(author); await db.SaveChangesAsync();
            var a = Judged(author.Id, 8); var b = Judged(author.Id, 2); b.SetHidden("llm", "llm 2: stored"); var c = new Post { Platform = "facebook", PlatformPostId = "unjudged", AuthorId = author.Id, Text = "synthetic", IngestReadyAt = Clock.Now };
            db.AddRange(a, b, c); await db.SaveChangesAsync(); visible = a.Id; hidden = b.Id; unjudged = c.Id;
        }
        var before = await Rows(i);
        var (counts, lines) = await Trial(i, "{\"score\":1,\"reason\":\"trial\",\"categories\":[]}");
        Assert.Equal(before, await Rows(i));
        Assert.Equal(2, counts.Selected); Assert.Equal(2, counts.Completed); Assert.Equal(0, counts.Failed);
        Assert.NotNull(counts.Trial); Assert.Equal(1, counts.Trial!.WouldHide); Assert.Equal(0, counts.Trial.WouldUnhide); Assert.Equal(1, counts.Trial.Unchanged); Assert.Equal(0, counts.Trial.CurrentVersion);
        var line = Assert.Single(lines, l => l.StartsWith("trial post")); Assert.Contains($"#{visible} facebook: score 8 -> 1, would hide", line); Assert.Contains("an older policy version", line);
        Assert.DoesNotContain(lines, l => l.Contains($"#{hidden} ")); Assert.DoesNotContain(lines, l => l.Contains($"#{unjudged} "));
    }

    [Fact]
    public async Task TrialReportsWouldUnhideAndLabelChanges()
    {
        await using var i = new TestInstance(); await i.Init(); long hidden, labelled;
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(author); await db.SaveChangesAsync();
            var a = Judged(author.Id, 2); a.SetHidden("llm", "llm 2: stored"); var b = Judged(author.Id, 9, "[\"stored\"]"); db.AddRange(a, b); await db.SaveChangesAsync(); hidden = a.Id; labelled = b.Id;
        }
        var before = await Rows(i);
        var (counts, lines) = await Trial(i, "{\"score\":9,\"reason\":\"trial\",\"categories\":[]}");
        Assert.Equal(before, await Rows(i));
        Assert.Equal(1, counts.Trial!.WouldUnhide); Assert.Equal(1, counts.Trial.LabelsChanged); Assert.Equal(0, counts.Trial.Unchanged);
        Assert.Contains(lines, l => l.Contains($"#{hidden} facebook: score 2 -> 9, would unhide"));
        Assert.Contains(lines, l => l.Contains($"#{labelled} facebook: score 9 -> 9, labels changed [stored] -> []"));
    }

    [Fact]
    public async Task TrialHonorsAnOwnerUnhide()
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(author); await db.SaveChangesAsync();
            var a = Judged(author.Id, 2); a.OwnerUnhiddenRevision = a.ContentRevision; db.Add(a); await db.SaveChangesAsync();
        }
        var before = await Rows(i);
        var (counts, lines) = await Trial(i, "{\"score\":1,\"reason\":\"trial\",\"categories\":[]}");
        Assert.Equal(before, await Rows(i)); Assert.Equal(0, counts.Trial!.WouldHide); Assert.Equal(1, counts.Trial.Unchanged); Assert.DoesNotContain(lines, l => l.StartsWith("trial post"));
    }

    [Fact]
    public void DisagreementPredicatesCoverBothDirections()
    {
        Post Judged(int score, bool llmHidden = false) { var p = new Post { Platform = "facebook", CategoriesJson = "[]", VerdictContentRevision = 1, LlmScore = score, LlmReason = "stored" }; if (llmHidden) p.SetHidden("llm", "llm"); return p; }
        Feedback Thumb(int value) => new() { Value = value };
        Assert.True(JudgeReport.Disagrees(Thumb(-1), Judged(8), 4));              // thumb down on a visible high-score post
        Assert.True(JudgeReport.Disagrees(Thumb(1), Judged(2, llmHidden: true), 4)); // thumb up on an llm hide
        Assert.True(JudgeReport.Disagrees(Thumb(1), Judged(2), 4));               // thumb up on a low-score visible post (unhid or shielded)
        Assert.False(JudgeReport.Disagrees(Thumb(1), Judged(8), 4));              // agreement
        Assert.False(JudgeReport.Disagrees(Thumb(-1), Judged(2, llmHidden: true), 4));
        Assert.False(JudgeReport.Disagrees(Thumb(-1), new Post { Platform = "facebook" }, 4)); // not judged: no score to disagree with
        var unhid = Judged(2); unhid.OwnerUnhiddenRevision = 1; Assert.True(JudgeReport.UnhideDisagrees(unhid, 4));
        Assert.False(JudgeReport.UnhideDisagrees(Judged(2), 4)); var high = Judged(8); high.OwnerUnhiddenRevision = 1; Assert.False(JudgeReport.UnhideDisagrees(high, 4));
    }

    [Fact]
    public async Task CurateReportGroupsByAuthorWithMemoryMarksAndUnhides()
    {
        await using var i = new TestInstance(); await i.Init(); long author, agreeing, disagreeing, unhid;
        await using (var db = i.Factory.Open())
        {
            var a = new Author { Platform = "facebook", DisplayName = "Synthetic One", IsFriend = true, RefsJson = "[\"fb:101\"]" }; db.Add(a); await db.SaveChangesAsync(); author = a.Id;
            var p1 = Judged(a.Id, 8); var p2 = Judged(a.Id, 8); var p3 = Judged(a.Id, 2); p3.OwnerUnhiddenRevision = p3.ContentRevision; db.AddRange(p1, p2, p3); await db.SaveChangesAsync(); agreeing = p1.Id; disagreeing = p2.Id; unhid = p3.Id;
            db.AddRange(new Feedback { PostId = p1.Id, Value = 1, AuthorId = a.Id, Platform = "facebook", ViewKey = "all" }, new Feedback { PostId = p2.Id, Value = -1, AuthorId = a.Id, Platform = "facebook", ViewKey = "all", CategoriesAtVote = "[]" }, new Feedback { PostId = p1.Id, Value = 1, AuthorId = a.Id, Platform = "facebook", ViewKey = "all", Curated = true });
            await db.SaveChangesAsync();
        }
        string[] lines; await using (var db = i.Factory.Open()) lines = await JudgeReport.Curate(db, Snapshot(), default);
        Assert.Equal($"author #{author} Synthetic One (facebook): 2 uncurated signals", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"  thumb #") && l.Contains($"post=#{disagreeing} value=-1") && l.EndsWith("DISAGREES with the current verdict (score 8, visible)"));
        Assert.Contains(lines, l => l.StartsWith($"  thumb #") && l.Contains($"post=#{agreeing} value=1") && !l.Contains("DISAGREES"));
        Assert.Contains(lines, l => l == "  memory: The owner voted up 2 posts by this author in the main feed.");
        Assert.Contains("unhides: 1 owner unhide against a low score", lines); Assert.Equal($"  post #{unhid} facebook: score 2; stored", lines[^1]);
        await using (var db = i.Factory.Open()) { await db.Feedback.ExecuteUpdateAsync(u => u.SetProperty(f => f.Curated, true)); await db.Posts.ExecuteUpdateAsync(u => u.SetProperty(p => p.OwnerUnhiddenRevision, (int?)null)); lines = await JudgeReport.Curate(db, Snapshot(), default); }
        Assert.Equal(["curate: no uncurated signals", "unhides: no owner unhide disagrees with the current verdict"], lines);
    }

    [Fact]
    public async Task HiddenScopeFiltersByOwner()
    {
        await using var i = new TestInstance(); await i.Init(); long llm;
        await using (var db = i.Factory.Open())
        {
            var a = Judged(0, 2); a.AuthorId = null; a.SetHidden("llm", "llm 2: stored"); var b = Judged(0, 8); b.AuthorId = null; b.SetHidden("keyword", "keyword: synthetic"); db.AddRange(a, b); await db.SaveChangesAsync(); llm = a.Id;
        }
        var s = new InstanceSnapshot(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()) }, new(), Preferences.Parse(""));
        var page = await new FeedQuery(i.Factory).Read(s, "all", null, "hidden", default, owner: "llm");
        Assert.Equal(llm, Assert.Single(page.Items).Lead.Post.Id); Assert.Equal(2, page.HiddenCount);
        var all = await new FeedQuery(i.Factory).Read(s, "all", null, "hidden", default); Assert.Equal(2, all.Items.Length);
        var none = await new FeedQuery(i.Factory).Read(s, "all", null, "hidden", default, owner: "mute"); Assert.Empty(none.Items); Assert.Equal("No posts hidden by mute.", none.EmptyMessage);
    }
}
