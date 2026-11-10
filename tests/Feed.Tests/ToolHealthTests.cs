using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Xunit;
namespace Feed.Tests;
public sealed class ToolHealthTests
{
    [Fact] public async Task AbsentToolIsMissingAndPresentToolReportsItsVersion()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        Assert.StartsWith("tools: not checked yet", await ToolHealth.Line(db));
        Assert.True(await ToolHealth.Available(db, "yt-dlp"));
        Assert.Equal("missing", (await ToolHealth.Check("feed-test-missing-tool", default)).Status);
        var present = await ToolHealth.Check("dotnet", default);
        Assert.Equal("ok", present.Status); Assert.Matches(@"^\d+\.", present.Version!);
        await db.Put(ToolHealth.Key("yt-dlp"), System.Text.Json.JsonSerializer.Serialize(new ToolHealth.State("error", null, "exit 1", Clock.Now), InstanceValidation.Json));
        Assert.False(await ToolHealth.Available(db, "yt-dlp")); Assert.StartsWith("tools: yt-dlp error (exit 1), checked ", await ToolHealth.Line(db));
    }
}
