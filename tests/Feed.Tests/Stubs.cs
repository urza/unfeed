using System.Net;
using System.Text;
using System.Text.Json;
namespace Feed.Tests;
// An HttpMessageHandler that answers every request with the given callback and counts the calls.
public sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    public int Calls;
    public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : this((r, _) => Task.FromResult(reply(r))) { }
    public StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : this((r, _) => send(r)) { }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Interlocked.Increment(ref Calls); return send(request, ct); }
}
public static class Replies
{
    // An OpenAI-style chat completion whose only message content is `content`.
    public static HttpResponseMessage Llm(string content) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { choices = new[] { new { message = new { content } } } }), Encoding.UTF8, "application/json") };
}
