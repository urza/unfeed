using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Feed.Core.Infrastructure;

public sealed record CaptureContext(int Version, string Source, string? Operation, string? EndpointFamily, int? Status, string? ContentType, string? Visit, string BodyHash)
{
    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
    public static void Publish(string path, string body, CaptureContext context)
    {
        InstancePaths.AtomicWrite(path + ".context", JsonSerializer.Serialize(context with { BodyHash = Hash(body) }));
        InstancePaths.AtomicWrite(path, body);
    }
    public static CaptureContext? Read(string path, string body)
    {
        if (!File.Exists(path + ".context")) return null;
        try
        {
            var context = JsonSerializer.Deserialize<CaptureContext>(File.ReadAllText(path + ".context"));
            if (context is null || context.Version != 1 || context.BodyHash != Hash(body) || context.Source is not ("network" or "embedded")) throw new FormatException("Capture context version/hash/source mismatch");
            return context;
        }
        catch (JsonException e) { throw new FormatException("Malformed capture context", e); }
    }
}
public sealed record CaptureDiagnostic(string Code, string Path, string Impact, string Evidence, int Root, string Fingerprint);
public sealed record CapturePart(int Root, string Kind, int Posts, bool ExplicitEmpty);
public static class CaptureDiagnostics
{
    public static string Shape(JsonElement root)
    {
        var paths = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(JsonElement node, string path, int depth)
        {
            if (depth > 7 || paths.Count >= 256) return;
            if (node.ValueKind == JsonValueKind.Object) foreach (var p in node.EnumerateObject())
            {
                // Keys can be dynamic IDs. Normalize them; never include scalar values.
                var key = System.Text.RegularExpressions.Regex.IsMatch(p.Name, @"^[A-Za-z_][A-Za-z_]*$") ? p.Name : "*";
                var next = path + "/" + key; paths.Add(next); Walk(p.Value, next, depth + 1);
            }
            else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray().Take(8)) Walk(item, path + "/*", depth + 1);
        }
        Walk(root, "", 0); return string.Join('|', paths);
    }
    public static CaptureDiagnostic Create(string platform, string? operation, JsonElement root, int index, string message, bool warning)
    {
        var code = message.StartsWith("parser uncertainty:", StringComparison.Ordinal) ? "unknown_response" : message.Contains("/GraphQL:", StringComparison.Ordinal) ? "upstream_error" : "invalid_json";
        var path = message.Contains("; path=", StringComparison.Ordinal) ? message.Split("; path=", 2)[1].Split(';')[0] : "";
        path = System.Text.RegularExpressions.Regex.Replace(path, @"(?<=/)[0-9]+(?=/|$)", "*");
        var impact = warning ? message.Contains("partial metadata unavailable", StringComparison.Ordinal) ? "partial" : "auxiliary"
            : PayloadParser.Walk(root).Any(n => PayloadParser.Get(n.Node, "post_id", "shortcode") is not null) ? "content" : "unknown_or_content";
        var fingerprint = CaptureContext.Hash(string.Join('\n', "v1", platform, operation ?? "legacy", code, path, impact, Shape(root)));
        return new(code, path, impact, message, index, fingerprint);
    }
}
