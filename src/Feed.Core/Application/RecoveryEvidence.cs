using System.Text.Json;
namespace Feed.Core.Application;

public sealed record EvidenceChoice(string Id, int Root, string Path);
public static class RecoveryEvidence
{
    public static string Json(string response)
    {
        var value = response.Trim();
        // Accept only a single complete JSON fence, not prose or a substring guessed to be JSON.
        if (value.StartsWith("```json\n", StringComparison.Ordinal) && value.EndsWith("\n```", StringComparison.Ordinal)) return value[8..^4].Trim();
        if (value.StartsWith("```\n", StringComparison.Ordinal) && value.EndsWith("\n```", StringComparison.Ordinal)) return value[4..^4].Trim();
        return value;
    }
    public static EvidenceChoice[] Choices(string body)
    {
        var choices = new List<EvidenceChoice>();
        var roots = CaptureRecovery.Roots(body);
        for (int i = 0; i < roots.Length; i++)
        {
            using var doc = JsonDocument.Parse(roots[i]);
            var queue = new Queue<(JsonElement Node, string Path, int Depth)>(); queue.Enqueue((doc.RootElement, "", 0));
            int count = 0;
            while (queue.TryDequeue(out var entry) && count++ < 256)
            {
                choices.Add(new("#" + choices.Count, i, entry.Path));
                if (entry.Depth >= 16) continue;
                if (entry.Node.ValueKind == JsonValueKind.Object)
                    foreach (var p in entry.Node.EnumerateObject()) queue.Enqueue((p.Value, entry.Path + "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), entry.Depth + 1));
                else if (entry.Node.ValueKind == JsonValueKind.Array)
                { int index = 0; foreach (var item in entry.Node.EnumerateArray()) queue.Enqueue((item, entry.Path + "/" + index++, entry.Depth + 1)); }
            }
        }
        return choices.ToArray();
    }
    public static RecoveryProposal Resolve(RecoveryProposal proposal, EvidenceChoice[] choices)
    {
        if (proposal.Decisions is null) throw new FormatException("Missing recovery decisions");
        var map = choices.ToDictionary(c => c.Id);
        return new(proposal.Decisions.Select(d => {
            string Resolve(string id) => map.TryGetValue(id, out var c) && c.Root == d.Root ? c.Path : throw new FormatException("Evidence choice is missing or belongs to another root");
            return d with { Evidence = d.Evidence?.Select(Resolve).ToArray() ?? throw new FormatException("Missing evidence choices"), Fields = d.Fields?.ToDictionary(f => f.Key, f => Resolve(f.Value)) };
        }).ToArray());
    }
}
