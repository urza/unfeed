using System.Diagnostics;
using System.Reflection;
using Feed.Cli;
using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Feed.Tests;

public sealed class LikeCliTests
{
    [Fact]
    public async Task ExplicitPostIdResolvesSqliteLongKeyBeforeCheckingOptIn()
    {
        await using var i = new TestInstance(); await i.Init();
        long id;
        await using (var db = i.Factory.Open())
        {
            var post = new Post { Platform = "facebook", PlatformPostId = "synthetic", Permalink = "https://www.facebook.com/synthetic/posts/101", LikeRef = "synthetic" };
            db.Posts.Add(post); await db.SaveChangesAsync(); id = post.Id;
        }
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        var configuration = typeof(CliProgram).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        var cli = Path.Combine(root!.FullName, "src", "Feed.Cli", "bin", configuration, "net10.0", "Feed.Cli.dll");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { cli, "like", "--data", i.Paths.Root, "--platform", "facebook", "--post-id", id.ToString() }) start.ArgumentList.Add(arg);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            var text = await output + await error;
            Assert.True(text.Contains("like-back is not enabled for facebook"), text);
            Assert.DoesNotContain("does not match the property type", text);
            Assert.NotEqual(0, child.ExitCode);
            await using var db = i.Factory.Open();
            Assert.Empty(await db.Likes.ToListAsync());
            Assert.Equal("facebook", (await db.Runs.SingleAsync()).Platform);
        }
        finally { if (!child.HasExited) { child.Kill(true); await child.WaitForExitAsync(); } }
    }
}
