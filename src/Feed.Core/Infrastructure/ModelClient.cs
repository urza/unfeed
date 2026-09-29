using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Feed.Core.Domain;
namespace Feed.Core.Infrastructure;

public sealed class TokenBudgetException(int budget, int reasoningChars) : FormatException($"model token budget exhausted (max_tokens={budget}, reasoning_chars={reasoningChars}); delayed budget escalation may retry this judgment")
{ public int Budget { get; } = budget; }

public sealed record ModelReply<T>(T Value, string Endpoint);
public sealed class ModelClient(HttpClient http)
{
    public static object RequestBody(LlmConfig config, object messages, int maxTokens, bool? enableThinking = null)
    {
        var body = new Dictionary<string, object> { ["model"] = config.Model, ["messages"] = messages, ["temperature"] = 0, ["max_tokens"] = maxTokens };
        if (enableThinking is { } enabled) body["chat_template_kwargs"] = new { enable_thinking = enabled };
        return body;
    }
    public async Task<ModelReply<T>> Call<T>(LlmConfig config, object messages, int maxTokens, Func<string, T> parse, CancellationToken ct, bool? enableThinking = null)
    {
        try { return await Send(config.BaseUrl, config.ApiKey); }
        catch (Exception e) when (!ct.IsCancellationRequested && config.FallbackBaseUrl.Length > 0 && (e is TaskCanceledException or TimeoutException or JsonException or FormatException || e is HttpRequestException h && (h.StatusCode is null || (int)h.StatusCode >= 500))) { return await Send(config.FallbackBaseUrl, config.FallbackApiKey); }
        async Task<ModelReply<T>> Send(string endpoint, string key)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.TrimEnd('/') + "/chat/completions");
            if (key.Length > 0) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            request.Content = new StringContent(JsonSerializer.Serialize(RequestBody(config, messages, maxTokens, enableThinking)), Encoding.UTF8, "application/json");
            try
            {
                using var response = await http.SendAsync(request, timeout.Token); response.EnsureSuccessStatusCode(); using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
                var choices = PayloadParser.At(doc.RootElement, "choices"); var choice = PayloadParser.Array(choices).FirstOrDefault(); var content = PayloadParser.Text(PayloadParser.At(choice, "message", "content"));
                var finish = PayloadParser.Get(choice, "finish_reason") ?? "unknown";
                var reasoning = PayloadParser.Get(PayloadParser.At(choice, "message"), "reasoning_content", "reasoning");
                if (finish == "length") throw new TokenBudgetException(maxTokens, reasoning?.Length ?? 0);
                if (string.IsNullOrWhiteSpace(content)) throw new FormatException($"model returned no final content (finish_reason={finish}, reasoning_chars={reasoning?.Length ?? 0}); reasoning is not a verdict");
                return new(parse(content), Application.Prompts.Endpoint(endpoint));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            { throw new TimeoutException($"model request timed out after {config.TimeoutSeconds}s at {Application.Prompts.Endpoint(endpoint)}; check endpoint load/availability or increase llm.timeout_seconds"); }
        }
    }
}
