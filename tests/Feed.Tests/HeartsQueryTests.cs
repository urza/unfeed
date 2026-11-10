using Feed.Core.Domain;
using Feed.Core.Queries;
using Xunit;
namespace Feed.Tests;
public sealed class HeartsQueryTests
{
    [Fact] public async Task RowsCarryThePersonAndOnlyAFailedPostWithoutAnActiveRowCanRetry()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        var author = new Author { Platform = "facebook", DisplayName = "Synthetic Person", RefsJson = "[\"fb:synthetic\"]" }; db.Add(author); await db.SaveChangesAsync();
        var liked = new Post { Platform = "facebook", PlatformPostId = "a", AuthorId = author.Id, Text = "Liked post", Permalink = "https://www.facebook.com/synthetic/posts/1", CategoriesJson = "[]" };
        var failed = new Post { Platform = "facebook", PlatformPostId = "b", ObservedAuthorName = "Observed Only", Text = new string('x', 100), CategoriesJson = "[]" };
        var retried = new Post { Platform = "facebook", PlatformPostId = "c", AuthorId = author.Id, CategoriesJson = "[]" };
        db.AddRange(liked, failed, retried); await db.SaveChangesAsync();
        db.AddRange(
            new Like { PostId = liked.Id, Platform = "facebook", State = "sent", SentAt = Clock.Now },
            new Like { PostId = failed.Id, Platform = "facebook", State = "failed", Error = "like button not found" },
            new Like { PostId = retried.Id, Platform = "facebook", State = "failed", Error = "like button not found" },
            new Like { PostId = retried.Id, Platform = "facebook", State = "pending" });
        await db.SaveChangesAsync();
        var rows = await HeartsQuery.Read(db);
        Assert.Equal(4, rows.Length); Assert.Equal("pending", rows[0].Like.State); // newest first
        var sent = Assert.Single(rows, r => r.Like.State == "sent"); Assert.Equal("Synthetic Person", sent.PersonName); Assert.Equal("Liked post", sent.Snippet); Assert.False(sent.CanRetry);
        var lost = Assert.Single(rows, r => r.Post.Id == failed.Id); Assert.Equal("Observed Only", lost.PersonName); Assert.Equal(81, lost.Snippet.Length); Assert.True(lost.CanRetry);
        Assert.All(rows.Where(r => r.Post.Id == retried.Id), r => Assert.False(r.CanRetry)); // a pending row blocks a new press
        Assert.Equal(2, (await HeartsQuery.Read(db, 2)).Length);
    }
}
