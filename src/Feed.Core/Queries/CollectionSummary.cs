using System.Text.Json;
using Feed.Core.Domain;
namespace Feed.Core.Queries;

public static class CollectionSummary
{
    public static string Describe(Run run)
    {
        string? scope = null; int? visited = null, incomplete = null;
        try
        {
            using var doc = JsonDocument.Parse(run.StatsJson ?? "{}");
            if (doc.RootElement.TryGetProperty("scope", out var value)) scope = value.GetString();
            if (doc.RootElement.TryGetProperty("visited", out value) && value.TryGetInt32(out var count)) visited = count;
            if (doc.RootElement.TryGetProperty("incomplete", out value) && value.TryGetInt32(out count)) incomplete = count;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { }
        scope ??= run.Trigger == "recovery" ? "retry" : run.Mode;
        var label = scope switch { "person" => "profile check", "retry" => "gap retry", "all_followed" => "followed-profile sweep", "close_friends" => "close-friends check", "home" => "home collection", _ => "collection" };
        var outcome = run.Status switch { "ok" => incomplete > 0 ? "incomplete" : "finished", "capped" => "limit reached", "error" => "errors", "checkpoint" => "login blocked", "cancelled" => "cancelled", _ => run.Status };
        return $"{run.Platform} {label} {FeedQuery.Relative(run.FinishedAt)} ago · {outcome} · +{run.PostsNew} new"
            + (visited > 0 ? $" · {visited} profiles visited" : "");
    }
}
