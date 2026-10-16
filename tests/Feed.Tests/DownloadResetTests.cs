using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;
public sealed class DownloadResetTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResetPreservesOwnerStateAndSupportsBothModes(bool deletePosts)
    {
        await using var i = new TestInstance(); await i.Init();
        string[] kept = ["config.json", "preferences.md", "taxonomy.json", "profiles/facebook/session", "media/avatars/person.jpg", "backgrounds/photo.jpg", "diagnostics/notes.txt"];
        string[] removed = ["raw/facebook/capture.json", "raw/instagram/capture.json", "media/facebook/post/photo.jpg", "media/instagram/post/video.mp4", "media/diagnostics/capture.png"];
        foreach (var file in kept.Concat(removed)) { Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(file))!); await File.WriteAllTextAsync(i.Paths.Get(file), "synthetic"); }
        await using (var db = i.Factory.Open())
        {
            var author = new Author { Platform = "facebook", IsFriend = true }; db.Add(author); await db.SaveChangesAsync();
            db.AuthorKeys.Add(new() { Key = "fb:synthetic", Platform = "facebook", AuthorId = author.Id });
            foreach (var platform in new[] { "facebook", "instagram" }) {
                var post = new Post { Platform = platform, PlatformPostId = "synthetic", AuthorId = author.Id, RawRef = "raw/" + platform + "/capture.json" };
                db.Add(post); await db.SaveChangesAsync();
                db.Media.Add(new() { PostId = post.Id, Path = "media/" + platform + "/post/photo.jpg" });
                db.Feedback.Add(new() { PostId = post.Id }); db.Likes.Add(new() { PostId = post.Id, Platform = platform });
                db.RawSnapshots.Add(new() { Platform = platform, Path = post.RawRef });
            }
            db.TimelineVisits.Add(new() { Platform = "facebook" }); db.SweepTargets.Add(new() { Platform = "facebook", CycleId = "old", AuthorId = author.Id });
            await db.Put("sweep:facebook:cycle", "old"); await db.Put(StorageCleanup.DeletedKey("instagram", "synthetic"), "1"); await db.Put("owner-setting", "keep");
            await db.SaveChangesAsync();
        }
        var cleanup = new StorageCleanup(i.Paths, i.Factory);
        using (var held = ResourceLock.Try(i.Paths, "instagram")) await Assert.ThrowsAsync<ResourceBusyException>(() => cleanup.ResetDownloads(deletePosts, default));
        Assert.All(removed, file => Assert.True(File.Exists(i.Paths.Get(file))));
        await cleanup.ResetDownloads(deletePosts, default);
        Assert.All(removed, file => Assert.False(File.Exists(i.Paths.Get(file))));
        foreach (var file in kept) Assert.Equal("synthetic", await File.ReadAllTextAsync(i.Paths.Get(file)));
        await using var check = i.Factory.Open();
        Assert.True((await check.Authors.SingleAsync()).IsFriend); Assert.Single(await check.AuthorKeys.ToListAsync());
        Assert.Empty(await check.RawSnapshots.ToListAsync()); Assert.Empty(await check.TimelineVisits.ToListAsync()); Assert.Empty(await check.SweepTargets.ToListAsync());
        Assert.Null(await check.Get("sweep:facebook:cycle")); Assert.Equal("keep", await check.Get("owner-setting"));
        Assert.Equal(deletePosts ? 0 : 2, await check.Posts.CountAsync());
        Assert.Equal(deletePosts ? 0 : 2, await check.Feedback.CountAsync()); Assert.Equal(deletePosts ? 0 : 2, await check.Likes.CountAsync());
        Assert.Equal(deletePosts ? null : "1", await check.Get(StorageCleanup.DeletedKey("instagram", "synthetic")));
        Assert.All(await check.Media.ToListAsync(), m => { Assert.Null(m.Path); Assert.NotNull(m.PrunedAt); });
        await cleanup.ResetDownloads(deletePosts, default);
        if (deletePosts)
        {
            // Recollection must not be suppressed by the old hidden-post marker.
            var igRaw = i.Paths.Get("raw", "instagram", "fresh.json");
            await File.WriteAllTextAsync(igRaw, """{"items":[{"pk":"synthetic","code":"synthetic","user":{"pk":"501","username":"fixture.person"},"caption":{"text":"Fresh synthetic caption"}}]}""");
            await new Ingest(i.Paths, i.Factory, new(i.Paths, new HttpClient())).File("instagram", igRaw, new InstanceFiles(new InstancePaths(i.Paths.Get("empty-settings"))).Current, true, default);
            Assert.Equal(1, await check.Posts.CountAsync());
        }
    }

    [Fact]
    public async Task RejectsSymlinksBeforeDeletingAnyFiles()
    {
        await using var i = new TestInstance(); await i.Init();
        var outside = i.Paths.Get("keep.txt"); await File.WriteAllTextAsync(outside, "keep");
        Directory.CreateDirectory(i.Paths.Get("raw")); File.CreateSymbolicLink(i.Paths.Get("raw", "link.json"), outside);
        await Assert.ThrowsAsync<IOException>(() => new StorageCleanup(i.Paths, i.Factory).ResetDownloads(true, default));
        Assert.Equal("keep", await File.ReadAllTextAsync(outside));
    }
}
