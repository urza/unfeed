using System.Collections.Immutable;
using Feed.Core.Domain;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;
public sealed class ViewTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task AuthorCategoryIntersectionRequiresBothAndCurrentVisibleVerdict(bool modelEnabled)
    {
        await using var i = new TestInstance(); await i.Init();
        await using (var db = i.Factory.Open())
        {
            var chosen = new Author { Platform = "facebook", DisplayName = "Fixture One", RefsJson = "[\"fb:fixture\"]" };
            var other = new Author { Platform = "facebook", DisplayName = "Fixture Other" };
            db.AddRange(chosen, other); await db.SaveChangesAsync();
            Post Row(string key, long? author, string? labels = "[\"topic\"]", int revision = 1, bool hidden = false) => new()
            { Platform = "facebook", PlatformPostId = key, AuthorId = author, PostedAt = Clock.Now, CategoriesJson = labels, VerdictContentRevision = revision, Hidden = hidden };
            db.Posts.AddRange(Row("match", chosen.Id), Row("wrong-category", chosen.Id, "[\"other\"]"), Row("unjudged", chosen.Id, null),
                Row("stale", chosen.Id, revision: 0), Row("hidden", chosen.Id, hidden: true), Row("outsider", other.Id), Row("unknown-author", null));
            await db.SaveChangesAsync();
        }
        var taxonomy = new Taxonomy
        {
            Categories = [new() { Key = "topic", Label = "Topic" }],
            Views = [new() { Key = "selected", Label = "Selected", Category = "topic", Authors = ["fb:fixture"] },
                new() { Key = "people", Label = "People", Authors = ["Fixture One"] },
                new() { Key = "empty", Label = "Empty", Category = "topic", Authors = [] },
                new() { Key = "combined", Label = "Combined", Union = ["selected", "people"] }]
        };
        InstanceValidation.Validate(new(), taxonomy);
        var snapshot = new InstanceSnapshot(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()), Llm = new() { Enabled = modelEnabled } }, taxonomy, Preferences.Parse(""));
        var query = new FeedQuery(i.Factory);
        var selected = await query.Read(snapshot, "selected", null, "live", default);
        Assert.Equal(1, selected.VisibleCount); Assert.Equal("match", Assert.Single(selected.Items).Lead.Post.PlatformPostId);
        Assert.Equal(4, (await query.Read(snapshot, "people", null, "live", default)).VisibleCount);
        var combined = await query.Read(snapshot, "combined", null, "live", default);
        Assert.Equal(4, combined.VisibleCount); Assert.Equal(4, combined.Items.Length);
        Assert.Equal("Selected", combined.Items.Single(c => c.Lead.Post.PlatformPostId == "match").Lead.Why);
        Assert.Equal("People", combined.Items.Single(c => c.Lead.Post.PlatformPostId == "unjudged").Lead.Why);
        var empty = await query.Read(snapshot, "empty", null, "live", default);
        Assert.Empty(empty.Items); Assert.Contains("judged as Topic", empty.EmptyMessage);
        Assert.Contains("category=topic AND authors=fb:fixture", await Reports.Rules(i.Factory, snapshot));
    }
    [Fact] public async Task RareWindowIncludesHiddenHistoryAndEqualTimeBoundaryAndUnionDeduplicates()
    {
        await using var i=new TestInstance();await i.Init();var at=Clock.Now;
        await using(var db=i.Factory.Open()){
            var a=new Author{Platform="facebook",IsFriend=true,DisplayName="Synthetic",RefsJson="[\"fb:fixture\"]"};db.Add(a);await db.SaveChangesAsync();
            db.Posts.AddRange(new(){Platform="facebook",PlatformPostId="boundary",AuthorId=a.Id,PostedAt=at.AddDays(-90),Hidden=true},new(){Platform="facebook",PlatformPostId="one",AuthorId=a.Id,PostedAt=at,CategoriesJson="[\"topic\"]",VerdictContentRevision=1},new(){Platform="facebook",PlatformPostId="two",AuthorId=a.Id,PostedAt=at,CategoriesJson="[\"topic\"]",VerdictContentRevision=1},new(){Platform="facebook",PlatformPostId="unjudged",AuthorId=a.Id,PostedAt=at.AddDays(1)});await db.SaveChangesAsync();
        }
        var taxonomy=new Taxonomy{Categories=[new(){Key="topic",Label="Topic",Definition="Synthetic"}],Views=[new(){Key="quiet",Label="Quiet",Rare=new(){MaxPosts=2,WindowDays=90}},new(){Key="category",Label="Topic",Category="topic"},new(){Key="combined",Label="Combined",Union=["quiet","category"]}]};
        var snapshot=new InstanceSnapshot(new(){Platforms=ImmutableDictionary<string,PlatformConfig>.Empty.Add("facebook",new()),Llm=new(){Enabled=true},Ui=new(){Stack=new(){MinPosts=0}}},taxonomy,Preferences.Parse(""));var query=new FeedQuery(i.Factory);
        Assert.Equal(0,(await query.Read(snapshot,"quiet",null,"live",default)).VisibleCount);
        var union=await query.Read(snapshot,"combined",null,"live",default);Assert.Equal(2,union.VisibleCount);Assert.All(union.Items,c=>Assert.Equal("Topic",c.Lead.Why));Assert.Equal(1,union.UnsortedCount);
        // Moving the old observation outside the inclusive boundary changes membership immediately.
        await using(var db=i.Factory.Open())await db.Posts.Where(p=>p.PlatformPostId=="boundary").ExecuteUpdateAsync(u=>u.SetProperty(p=>p.PostedAt,at.AddDays(-90).AddSeconds(-1)));
        Assert.Equal(2,(await query.Read(snapshot,"quiet",null,"live",default)).VisibleCount);
    }
}
