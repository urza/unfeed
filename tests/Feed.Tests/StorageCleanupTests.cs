using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;
public sealed class StorageCleanupTests
{
    [Fact] public async Task PreviewDeletionAndReplayPreserveVisibleContent()
    {
        await using var i = new TestInstance(); await i.Init();
        var raw = i.Paths.Get("raw", "instagram", "fixture", "001.json"); Directory.CreateDirectory(Path.GetDirectoryName(raw)!);
        await File.WriteAllTextAsync(raw, """{"items":[{"pk":"101","code":"synthetic","user":{"pk":"501","username":"fixture.person"},"caption":{"text":"Synthetic caption"}}]}""");
        var ingest = new Ingest(i.Paths, i.Factory, new(i.Paths, new HttpClient())); var snapshot = new InstanceFiles(i.Paths).Current;
        await ingest.File("instagram", raw, snapshot, true, default);
        var hiddenPath = "media/instagram/101/01.jpg"; var visiblePath = "media/instagram/visible/01.jpg";
        foreach (var path in new[] { hiddenPath, visiblePath }) { Directory.CreateDirectory(Path.GetDirectoryName(i.Paths.Get(path))!); await File.WriteAllBytesAsync(i.Paths.Get(path), [1,2,3]); }
        await using (var db = i.Factory.Open()) {
            var hidden = await db.Posts.SingleAsync(); Assert.True(hidden.Hidden);
            var visible = new Post { Platform = "instagram", PlatformPostId = "visible" }; db.Add(visible); await db.SaveChangesAsync();
            db.Media.Add(new() { PostId = hidden.Id, Path = hiddenPath }); db.Media.Add(new() { PostId = hidden.Id, Path = hiddenPath, IsCurrent = false });
            db.Media.Add(new() { PostId = hidden.Id, Path = visiblePath }); db.Media.Add(new() { PostId = visible.Id, Path = visiblePath });
            db.Feedback.Add(new() { PostId = hidden.Id }); db.Likes.Add(new() { PostId = hidden.Id, Platform = "instagram" }); await db.SaveChangesAsync();
        }
        var cleanup = new StorageCleanup(i.Paths, i.Factory); var preview = await cleanup.HiddenPosts(true, default);
        Assert.Equal(1, preview.Posts); Assert.Equal(1, preview.Files); Assert.Equal(3, preview.Bytes); Assert.True(File.Exists(i.Paths.Get(hiddenPath)));
        using (var held = ResourceLock.Try(i.Paths, "instagram")) await Assert.ThrowsAsync<ResourceBusyException>(() => cleanup.HiddenPosts(false, default));
        Assert.True(File.Exists(i.Paths.Get(hiddenPath)));
        var result = await cleanup.HiddenPosts(false, default); Assert.Equal(preview with { Preview = false }, result);
        Assert.False(File.Exists(i.Paths.Get(hiddenPath))); Assert.True(File.Exists(i.Paths.Get(visiblePath))); Assert.True(File.Exists(raw));
        await ingest.File("instagram", raw, snapshot, true, default);
        await using var check = i.Factory.Open(); Assert.Equal("visible", (await check.Posts.SingleAsync()).PlatformPostId);
        Assert.Single(await check.Media.ToListAsync()); Assert.Empty(await check.Feedback.ToListAsync()); Assert.Empty(await check.Likes.ToListAsync());
        Assert.NotNull(await check.Get(StorageCleanup.DeletedKey("instagram", "101")));
        Assert.Equal(0, (await cleanup.HiddenPosts(false, default)).Posts);
    }
}
