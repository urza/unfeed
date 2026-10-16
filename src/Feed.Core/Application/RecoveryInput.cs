using System.Text.Json;
using Feed.Core.Infrastructure;
namespace Feed.Core.Application;

// JSONL roots are independently parsed envelopes. Never cut through a post or
// strip its policy/context fields to fit a request. Original root indices remain audited.
public sealed record RecoveryInput(string Body, int[] SourceRoots, EvidenceChoice[] Choices, object[] Messages)
{
    public static RecoveryInput? Build(string platform, string body, CaptureContext? context, ParsedCapture current, int maxChars)
    {
        var roots = CaptureRecovery.Roots(body);
        var selected = new List<int>(); RecoveryInput? result = null;
        foreach (var index in current.Issues.Where(i => i.Impact is not ("auxiliary" or "partial")).Select(i => i.Root).Distinct().Order())
        {
            if (index < 0 || index >= roots.Length || roots[index].Length > maxChars) continue;
            try { using var doc = JsonDocument.Parse(roots[index]); } catch (JsonException) { continue; }
            var indices = selected.Append(index).ToArray(); var subset = string.Join('\n', indices.Select(i => roots[i]));
            var choices = RecoveryEvidence.Choices(subset);
            object[] messages = [new { role = "system", content = CaptureRecovery.Instructions }, new { role = "user", content = JsonSerializer.Serialize(new {
                platform, context, source_root_indices = indices, source_body_hash = CaptureContext.Hash(body),
                parser_issues = current.Issues.Where(i => indices.Contains(i.Root)), evidence_choices = choices, capture = subset
            }) }];
            if (JsonSerializer.Serialize(messages).Length > maxChars) continue;
            selected.Add(index); result = new(subset, indices, choices, messages);
        }
        return result;
    }
    public RecoveryProposal Merge(string originalBody, RecoveryProposal proposal, RecoveryProposal? previous = null)
    {
        if (proposal.Decisions.Length != SourceRoots.Length || !proposal.Decisions.Select(d => d.Root).Order().SequenceEqual(Enumerable.Range(0, SourceRoots.Length)))
            throw new FormatException("Exactly one decision per selected root required");
        var decisions = Enumerable.Range(0, CaptureRecovery.Roots(originalBody).Length)
            .Select(i => previous?.Decisions.SingleOrDefault(d => d.Root == i) ?? new RecoveryDecision(i, "unknown", "Root not selected for this bounded request", [""])).ToArray();
        foreach (var decision in proposal.Decisions) decisions[SourceRoots[decision.Root]] = decision with { Root = SourceRoots[decision.Root] };
        return new(decisions);
    }
}
