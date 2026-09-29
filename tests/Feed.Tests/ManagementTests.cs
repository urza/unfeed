using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Web;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using System.Text.Json.Nodes;
using Xunit;

namespace Feed.Tests;

public sealed class ManagementTests
{
    static FormCollection Form(params (string Key, string Value)[] values) => new(values.ToDictionary(x => x.Key, x => new StringValues(x.Value)));
    [Fact]
    public async Task ScheduleSavePreservesUnrelatedSettingsRejectsStaleFormsAndValidatesBeforeWriting()
    {
        await using var i = new TestInstance(); await i.Init();
        var path = i.Paths.Get("config.json");
        await File.WriteAllTextAsync(path, """{"timezone":"UTC","llm":{"api_key":"synthetic-secret","base_url":"http://synthetic.test/v1"},"platforms":{"instagram":{"close_friends":["ig:101"],"schedule":{"HOME":["01:00"],"close_friends":["09:00"]}}}}""");
        var files = new ManagementFiles(i.Paths); var management = new Management(i.Paths, i.Factory, files); var version = files.Read().Version;
        var form = Form(("version",version),("platform","instagram"),("mode","home"),("times","08:00, 18:00"),("interval","2"),("start","2028-01-01"),("days","mon"));
        await management.SaveSettings("schedule", form, default);
        var saved = files.Read().Snapshot(); Assert.Equal("synthetic-secret", saved.Config.Llm.ApiKey); Assert.Equal<string>(["ig:101"], saved.Config.Platform("instagram").CloseFriends);
        Assert.Equal<string>(["09:00"], saved.Config.Platform("instagram").Schedule["close_friends"]); Assert.Equal<string>(["08:00","18:00"], saved.Config.Platform("instagram").Schedule["home"]);
        Assert.Equal(2, saved.Config.Platform("instagram").ScheduleIntervals["home"].Days);
        Assert.Single(Directory.GetFiles(i.Paths.Get("settings-backups")));
        await Assert.ThrowsAsync<SettingsConflictException>(() => management.SaveSettings("schedule", form, default));
        var before = File.ReadAllText(path);
        await Assert.ThrowsAsync<FormatException>(() => management.SaveSettings("schedule", Form(("version",files.Read().Version),("platform","instagram"),("mode","home"),("times","25:00"),("interval","1"),("start","")), default));
        Assert.Equal(before, File.ReadAllText(path));
        await File.AppendAllTextAsync(path, "\n");
        Assert.Throws<SettingsConflictException>(() => files.Save(Prompts.Sha("stale"), d => d));
    }
    [Fact]
    public async Task UnmutingOneNameMatchKeepsOtherPeopleMutedAndRestoresModelVisibilityGate()
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"facebook":{}}}""");
        var prefs = "Intro retained\n## Muted people\n<!-- keep this comment -->\n- mute: Synthetic\n## Always show\n- show: fb:101\n## Plain-English policy (LLM scoring layer)\n- Keep relevant posts\n";
        await File.WriteAllTextAsync(i.Paths.Get("preferences.md"), prefs);
        long id;
        await using (var db = i.Factory.Open()) {
            var a = new Author { Platform = "facebook", DisplayName = "Synthetic One", IsFriend = true, RefsJson = "[\"fb:101\"]" };
            var b = new Author { Platform = "facebook", DisplayName = "Synthetic Two", IsFriend = true, RefsJson = "[\"fb:102\"]" }; db.AddRange(a,b); await db.SaveChangesAsync(); id = a.Id;
            db.AddRange(new Post { Platform = "facebook", PlatformPostId = "one", AuthorId = a.Id, Hidden = true, HiddenBy = "mute", CategoriesJson = "[]", VerdictContentRevision = 1, LlmScore = 2 },new Post { Platform = "facebook", PlatformPostId = "two", AuthorId = b.Id, Hidden = true, HiddenBy = "mute" }); await db.SaveChangesAsync();
        }
        var files = new ManagementFiles(i.Paths); var management = new Management(i.Paths, i.Factory, files);
        await management.Person(id,"always",false,files.Read().Version,default);
        await management.Person(id,"mute",false,files.Read().Version,default);
        var after = files.Read(); Assert.Equal<string>(["fb:102"], after.Snapshot().Preferences.Mutes); Assert.Contains("Intro retained",after.Preferences); Assert.Contains("<!-- keep this comment -->",after.Preferences);
        await using var check = i.Factory.Open(); var post = await check.Posts.SingleAsync(p => p.AuthorId == id); Assert.True(post.Hidden); Assert.Equal("llm",post.HiddenBy); Assert.True(post.VisibilityRevision > 0);
        Assert.Equal("mute",(await check.Posts.SingleAsync(p => p.AuthorId != id)).HiddenBy);
    }
    [Fact]
    public async Task RemovingPersonPreservesHistoryAndOtherNameMatchesWhileRemovingSweepAndCloseFriendMembership()
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{"instagram":{"close_friends":["Synthetic"],"home_timeline_authors":["Synthetic"]}}}""");
        await File.WriteAllTextAsync(i.Paths.Get("taxonomy.json"), """{"categories":[{"key":"life","close_friends_only":["instagram"]}]}""");
        long id;
        await using(var db=i.Factory.Open()) {
            var a = new Author { Platform="instagram", DisplayName="Synthetic One", IsFriend=true, RefsJson="[\"ig:101\"]" };var b = new Author { Platform="instagram", DisplayName="Synthetic Two", IsFriend=true, RefsJson="[\"ig:102\"]" };db.AddRange(a,b);await db.SaveChangesAsync();id=a.Id;
            db.Add(new Post { Platform="instagram",PlatformPostId="fixture",AuthorId=a.Id,CategoriesJson="[\"life\"]",VerdictContentRevision=1,LlmScore=9 });
            db.Add(new SweepTarget { Platform="instagram",CycleId="fixture",AuthorId=a.Id,State="pending" });await db.SaveChangesAsync();
        }
        var files=new ManagementFiles(i.Paths);var management=new Management(i.Paths,i.Factory,files);await management.Person(id,"remove",false,files.Read().Version,default);
        var s=files.Read().Snapshot();Assert.Equal<string>(["ig:102"],s.Config.Platform("instagram").CloseFriends);Assert.Equal<string>(["ig:102"],s.Config.Platform("instagram").HomeTimelineAuthors);
        await using var check=i.Factory.Open();Assert.False((await check.Authors.FindAsync(id))!.IsFriend);var post=await check.Posts.SingleAsync();Assert.True(post.Hidden);Assert.Equal("whitelist",post.HiddenBy);Assert.Equal("[]",post.CategoriesJson);Assert.Equal("skipped",(await check.SweepTargets.SingleAsync()).State);
    }
    [Fact]
    public void WrittenPolicyEditorRecognizesExistingHeadingsAndPreservesComments()
    {
        var original="# Policy\n## Plain-English policy (LLM scoring layer)\nProse\n<!--\n- not a live rule\n-->\n- Old rule\n## Muted people\n- mute: ig:synthetic\n";
        var edited=ManagementFiles.PreferenceSection(original,"Plain-English policy","",["New rule"]);
        Assert.Equal<string>(["New rule"],Preferences.Parse(edited).Policy);Assert.Contains("Prose",edited);Assert.Contains("- not a live rule",edited);Assert.Equal<string>(["ig:synthetic"],Preferences.Parse(edited).Mutes);
    }
    [Fact]
    public async Task PeopleRefreshQueueDeduplicatesAndSchedulerHonorsPauseBrowserAndLoginGates()
    {
        await using var i=new TestInstance();await i.Init();var path=i.Paths.Get("config.json");
        await File.WriteAllTextAsync(path,"""{"platforms":{"instagram":{}},"scheduler":{"enabled":false},"backgrounds":{"source":"off"}}""");
        var files=new ManagementFiles(i.Paths);var management=new Management(i.Paths,i.Factory,files);
        await Assert.ThrowsAsync<FormatException>(()=>management.Queue("instagram","friends",null,default));
        await File.WriteAllTextAsync(path,"""{"platforms":{"instagram":{}},"backgrounds":{"source":"off"}}""");
        await management.Queue("instagram","friends",null,default);await management.Queue("instagram","friends",null,default);
        using var processing=ResourceLock.Try(i.Paths,"processing");
        await using(var db=i.Factory.Open()){Assert.Single(await db.RunRequests.ToArrayAsync());await db.Put("maintenance:last",Clock.Now.ToString("yyyy-MM-dd"));}
        var scheduler=new Scheduler(i.Paths,new(i.Paths),i.Factory,NullLogger<Scheduler>.Instance,()=>i.Paths.Get("missing-cli"));
        using(var browser=ResourceLock.Try(i.Paths,"instagram")){await scheduler.Tick(default);await using var db=i.Factory.Open();Assert.Equal("pending",(await db.RunRequests.SingleAsync()).Status);}
        await Assert.ThrowsAsync<FileNotFoundException>(()=>scheduler.Tick(default));
        await using(var db=i.Factory.Open()){Assert.Equal("refused",(await db.RunRequests.SingleAsync()).Status);db.PlatformStates.Add(new(){Platform="instagram",NeedsRelogin=true});await db.SaveChangesAsync();}
        await Assert.ThrowsAsync<FormatException>(()=>management.Queue("instagram","friends",null,default));
        await management.Queue("instagram","login",null,default);await Assert.ThrowsAsync<FileNotFoundException>(()=>scheduler.Tick(default));
        await using(var db=i.Factory.Open()){Assert.Equal("refused",(await db.RunRequests.SingleAsync(r=>r.Kind=="login")).Status);}
    }
    [Fact]
    public async Task InterruptedRefilterResumesEvenWhileSchedulingIsPaused()
    {
        await using var i=new TestInstance();await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"),"""{"platforms":{"facebook":{}},"scheduler":{"enabled":false}}""");
        await File.WriteAllTextAsync(i.Paths.Get("preferences.md"),"## Muted people\n- mute: fb:101\n");
        await using(var db=i.Factory.Open()) {
            var a=new Author {Platform="facebook",RefsJson="[\"fb:101\"]",IsFriend=true};db.Add(a);await db.SaveChangesAsync();
            db.Add(new Post {Platform="facebook",PlatformPostId="fixture",AuthorId=a.Id});await db.SaveChangesAsync();await db.Put("management:refilter_pending","1");
        }
        var scheduler=new Scheduler(i.Paths,new(i.Paths),i.Factory,NullLogger<Scheduler>.Instance,()=>throw new Exception("Must not launch"));
        await scheduler.Tick(default);
        await using var check=i.Factory.Open();Assert.Equal("mute",(await check.Posts.SingleAsync()).HiddenBy);Assert.Null(await check.Get("management:refilter_pending"));Assert.Empty(await check.RunRequests.ToArrayAsync());
    }
    [Fact]
    public void PreferenceEditingIgnoresCommentedHeadingsAndReplacesDuplicateActiveSections()
    {
        var original="<!--\n## Muted people\n- mute: comment\n-->\n## Muted people\n- mute: ig:first\n## Muted people (extra)\n- mute: ig:second\n";
        var result=ManagementFiles.PreferenceSection(original,"Muted people","mute: ",["ig:final"]);
        Assert.Equal<string>(["ig:final"],Preferences.Parse(result).Mutes);Assert.Contains("- mute: comment",result);
    }
    [Fact]
    public async Task RequestMigrationPreservesExistingClaimsAndAddsFriendsAndLoginUniqueness()
    {
        await using var i=new TestInstance();await i.Init();
        await using var db=i.Factory.Open();var migrator=db.GetService<IMigrator>();
        await migrator.MigrateAsync("20260929065137_JudgmentTokenRetry");
        db.RunRequests.Add(new(){Kind="collect",Platform="facebook",Mode="home",Status="claimed",ClaimToken="synthetic-token",ChildPid=123,RetryIncomplete=true});await db.SaveChangesAsync();db.ChangeTracker.Clear();
        await migrator.MigrateAsync();var request=await db.RunRequests.SingleAsync();Assert.Equal("synthetic-token",request.ClaimToken);Assert.Equal(123,request.ChildPid);Assert.True(request.RetryIncomplete);
        Assert.Equal(1,await Actions.EnsureRequest(db,"friends","facebook"));Assert.Equal(0,await Actions.EnsureRequest(db,"friends","facebook"));Assert.Equal(1,await Actions.EnsureRequest(db,"login","facebook"));
        Assert.Equal(3,await db.RunRequests.CountAsync());
    }
    [Fact]
    public void NextRunHonorsIntervalsWeekdaysFiredSlotsAndPauses()
    {
        var c=InstanceValidation.Parse<FeedConfig>("""{"timezone":"UTC","platforms":{"facebook":{"schedule":{"home":["08:00"]},"schedule_intervals":{"home":{"days":2,"start_date":"2028-02-28"}}}},"scheduler":{"jitter_minutes":0}}""");
        Assert.Contains("01 Mar 2028 08:00",Management.NextRun(c,"facebook",new(2028,2,28,8,1,0,DateTimeKind.Utc),new Dictionary<string,string>{{"slot:facebook:home:08:00","2028-02-28"}}));
        Assert.Equal("Scheduling paused",Management.NextRun(c with {Scheduler=c.Scheduler with {Enabled=false}},"facebook",Clock.Now,new Dictionary<string,string>()));
    }
}
