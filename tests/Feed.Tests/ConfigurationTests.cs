using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Xunit;
namespace Feed.Tests;
public sealed class ConfigurationTests
{
    [Theory]
    [InlineData("\"category\":\"topic\",\"authors\":[\"fb:fixture\"]", true)]
    [InlineData("\"category\":\"topic\",\"authors\":[]", true)]
    [InlineData("\"category\":\"unknown\",\"authors\":[]", false)]
    [InlineData("\"category\":\"topic\",\"authors\":[],\"rare\":{}", false)]
    [InlineData("\"category\":\"topic\",\"authors\":[],\"union\":[]", false)]
    [InlineData("\"authors\":[],\"rare\":{}", false)]
    [InlineData("\"category\":\"topic\",\"union\":[]", false)]
    [InlineData("\"label\":\"Empty\"", false)]
    public void CategoryCanConstrainAuthorsButOtherMixedShapesAreRejected(string fields, bool valid)
    {
        var taxonomy = InstanceValidation.Parse<Taxonomy>("{\"categories\":[{\"key\":\"topic\"}],\"views\":[{\"key\":\"selected\"," + fields + "}]}");
        if (valid) InstanceValidation.Validate(new(), taxonomy);
        else Assert.Throws<FormatException>(() => InstanceValidation.Validate(new(), taxonomy));
    }
    [Fact] public void MissingFilesPausePlatformsAndModel() { var c = InstanceValidation.Parse<FeedConfig>(null); Assert.All(Platforms.All, p => Assert.False(c.Enabled(p))); Assert.False(c.Llm.Enabled); Assert.Equal(300, c.Ui.RenderCap); }
    [Theory] [InlineData("{\"typo\":1}")] [InlineData("{\"llm\":{\"typo\":1}}")] [InlineData("{\"ui\":null}")] public void InvalidShapeFails(string json) => Assert.ThrowsAny<Exception>(() => InstanceValidation.Parse<FeedConfig>(json));
    [Fact] public void CommentsAndCaseInsensitiveKeysWork() { var c = InstanceValidation.Parse<FeedConfig>("{// comment\n\"UI\":{\"render_cap\":100000,},}"); Assert.Equal(100000, c.Ui.RenderCap); InstanceValidation.Validate(c, new()); }
    [Fact] public void InvalidReloadKeepsWholeSnapshot() { var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()); Directory.CreateDirectory(root); try { var f = new InstanceFiles(new(root)); var before = f.Current; File.WriteAllText(Path.Combine(root, "config.json"), "{\"ui\":{\"render_cap\":0}}"); Assert.Same(before, f.Refresh()); Assert.NotNull(f.Error); File.WriteAllText(Path.Combine(root, "config.json"), "{\"scheduler\":{\"enabled\":false}}"); Assert.False(f.Refresh().Config.Scheduler.Enabled); Assert.Null(f.Error); } finally { Directory.Delete(root, true); } }
    [Fact] public void PreferencesHonorSectionsAndRejectBadMachineBullets() { var p = Preferences.Parse("## Always show\n- prose ignored\n- show: ig:synthetic\n<!--\n## Muted people\n- wrong\n-->\n## Plain-English policy\n- Synthetic policy.\n## Never show — my rules\n- keyword: synthetic"); Assert.Single(p.Shows); Assert.Single(p.Policy); Assert.Single(p.Keywords); Assert.Equal(2, p.HashLines.Length); Assert.Throws<FormatException>(() => Preferences.Parse("## Muted people\n- typo")); }
    [Fact] public void UnionOfUnionsIsRejected() { var t = InstanceValidation.Parse<Taxonomy>("{\"views\":[{\"key\":\"a\",\"authors\":[]},{\"key\":\"b\",\"union\":[\"a\"]},{\"key\":\"c\",\"union\":[\"b\"]}]}"); Assert.Throws<FormatException>(() => InstanceValidation.Validate(new(), t)); }
    [Theory] [InlineData("facebook", "facebook.com/profile.php?id=123", "fb:123")] [InlineData("instagram", "https://www.instagram.com/synthetic/", "ig:synthetic")] [InlineData("facebook", "https://facebook.com/groups/123", null)] [InlineData("facebook", "https://facebook.com.evil.test/name", null)] public void UrlReferences(string p, string url, string? expected) => Assert.Equal(expected, Identity.UrlRef(p, url));
    [Fact] public void NameMatchingUsesAccentsAndAllTokens() { Assert.True(Identity.NameMatches("Zofie Novakovi", "Žofie Nováková")); Assert.False(Identity.NameMatches("Zofie Other", "Žofie Nováková")); }
    [Fact] public void VisibilityOwnsDatesAndRevisions() { var p = new Post(); p.SetHidden("keyword", "one"); var at = p.HiddenAt; p.SetHidden("mute", "two"); Assert.Equal(at, p.HiddenAt); Assert.Equal(2, p.VisibilityRevision); p.ClearHidden(); Assert.Null(p.HiddenAt); Assert.Null(p.HiddenBy); Assert.False(p.Hidden); Assert.Equal(3, p.VisibilityRevision); }
    [Fact] public void OppositeInstancesApplyOwnTypePolicy() { var blocked = new InstanceSnapshot(new() { Filters = new() { Audience = "all_captured", BlockedTypes = ["reel"] } }, new(), Preferences.Parse("")); var permitted = blocked with { Config = blocked.Config with { Filters = blocked.Config.Filters with { BlockedTypes = [] } } }; var p = new Post { Platform = "instagram", IsReel = true }; Filters.Apply(p, new(null, [], blocked)); Assert.Equal("structural", p.HiddenBy); Filters.Apply(p, new(null, [], permitted)); Assert.False(p.Hidden); }
}
