using System.Collections.Immutable;
using System.Net;
using System.Text;
using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Core.Queries;
using Microsoft.EntityFrameworkCore;
using Xunit;
namespace Feed.Tests;

public sealed class JudgmentRetryTests
{
    sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request); }
    static HttpResponseMessage Reply(bool length) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = length ? "length" : "stop", message = new { content = length ? "" : "{\"score\":7,\"reason\":\"synthetic\",\"categories\":[]}" } } } }), Encoding.UTF8, "application/json") };
    [Fact] public async Task DelayedLadderPersistsCapsAndUsesActualRequestHash()
    {
        await using var i = new TestInstance(); await i.Init();
        var s = new InstanceSnapshot(new() { Platforms = ImmutableDictionary<string, PlatformConfig>.Empty.Add("facebook", new()), Llm = new() { Enabled = true, Vision = false, BaseUrl = "http://model.test/v1", TokenRetryDelayMinutes = 45 } }, new(), Preferences.Parse(""));
        await using (var db = i.Factory.Open()) { db.Add(new Post { Platform = "facebook", PlatformPostId = "synthetic", IngestReadyAt = Clock.Now, CategoriesJson = "[]", VerdictContentRevision = 1, Summary = "", SummaryContentRevision = 1 }); await db.SaveChangesAsync(); }
        var budgets = new List<int>(); string lastBody = ""; bool fail = true;
        using var http = new HttpClient(new Handler(async r => { lastBody = await r.Content!.ReadAsStringAsync(); using var body = JsonDocument.Parse(lastBody); budgets.Add(body.RootElement.GetProperty("max_tokens").GetInt32()); return Reply(fail); }));
        async Task<WorkCounts> Run(string kind = "process") { var media = new MediaFiles(i.Paths, http); return await new Processing(i.Paths, i.Factory, new(http), media, new(i.Paths, i.Factory, media)).Run(s, new(kind, PostId: kind == "rescore" ? 1 : null), null, default); }
        async Task Age(int minutes) { await using var db = i.Factory.Open(); await db.Posts.ExecuteUpdateAsync(u => u.SetProperty(p => p.LlmAttemptedAt, Clock.Now.AddMinutes(-minutes))); }
        Assert.Equal(1, (await Run("rescore")).Failed); // Current-content verdicts must also recover a failed explicit rejudgment.
        Assert.Equal(0, (await Run()).Selected);
        await Age(31); Assert.Equal(0, (await Run()).Selected);
        await Age(46); Assert.Equal(1, (await Run()).Failed);
        await Age(46); Assert.Equal(1, (await Run()).Failed);
        await Age(46); Assert.Equal(0, (await Run()).Selected);
        Assert.Equal(new[] { 4000, 8000, 16000 }, budgets);
        await using (var db = i.Factory.Open())
        {
            var p = await db.Posts.SingleAsync(); Assert.False(p.Hidden); Assert.Equal("[]", p.CategoriesJson); Assert.Equal(16000, p.LlmTokenLimit);
            Assert.False(await db.Posts.AnyAsync(JudgmentRetry.Due(s.Config.Llm, Clock.Now)));
            Assert.Contains("exhausted", Assert.Single(await RecoveryQuery.Read(db, s)).Recovery);
            Assert.Null(await db.Get($"model:{Prompts.ConfigurationHash(s)}:judge:not_before"));
        }
        s = s with { Config = s.Config with { Llm = s.Config.Llm with { TokenRetryBudgets = [8000, 16000, 24000] } } }; fail = false;
        Assert.Equal(1, (await Run()).Completed); Assert.Equal(24000, budgets.Last());
        await using (var db = i.Factory.Open()) { var p = await db.Posts.SingleAsync(); Assert.Null(p.LlmTokenLimit); Assert.Null(p.LlmError); Assert.Equal(Prompts.Sha(lastBody), p.VerdictInputHash); }
        Assert.Equal(4000, s.Config.Llm.MaxTokens);
    }
    [Fact] public void ConfigurationAndBudgetSelectionAreBounded()
    {
        var c = new LlmConfig { TokenRetryBudgets = [] }; var p = new Post { LlmTokenLimit = 4000 };
        Assert.Null(JudgmentRetry.Budget(p, c)); Assert.False(JudgmentRetry.Due(c, Clock.Now).Compile()(p));
        Assert.Equal(6000, JudgmentRetry.Budget(p, c with { MaxTokens = 6000 }));
        Assert.Equal(16000, JudgmentRetry.Budget(p, c with { TokenRetryBudgets = [16000] }));
        foreach (var budgets in new[] { ImmutableArray.Create(0), ImmutableArray.Create(8000, 8000), ImmutableArray.Create(16000, 8000) })
            Assert.Throws<FormatException>(() => InstanceValidation.Validate(new() { Llm = c with { TokenRetryBudgets = budgets } }, new()));
        Assert.Throws<FormatException>(() => InstanceValidation.Validate(new() { Llm = c with { TokenRetryDelayMinutes = 0 } }, new()));
        InstanceValidation.Validate(new() { Llm = c }, new());
    }
    [Fact] public async Task OnlyLengthResponseIsTypedAsTokenExhaustion()
    {
        using var http = new HttpClient(new Handler(_ => Task.FromResult(Reply(true))));
        var error = await Assert.ThrowsAsync<TokenBudgetException>(() => new ModelClient(http).Call(new() { BaseUrl = "http://model.test/v1" }, Array.Empty<object>(), 6000, s => s, default));
        Assert.Equal(6000, error.Budget);
        using var bad = new HttpClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))));
        await Assert.ThrowsAsync<HttpRequestException>(() => new ModelClient(bad).Call(new() { BaseUrl = "http://model.test/v1" }, Array.Empty<object>(), 6000, s => s, default));
    }
}
