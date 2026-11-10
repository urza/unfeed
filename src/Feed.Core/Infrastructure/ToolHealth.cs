using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using Feed.Core.Domain;

namespace Feed.Core.Infrastructure;

// Processing spawns yt-dlp and ffmpeg per post. When one is absent every item fails with no signal: the live
// instance held 256 video-context cache entries, all tool_missing, before plan 10. The processing child checks
// both tools once per run and leaves the result in Kv, where status, the Manage processing panel and the recovery
// rows read it. The web host never spawns a tool (binding rules 3 and 8), so the keys stay absent until the first
// processing run, and an absent key is "not checked yet", never an alarm.
public static class ToolHealth
{
    public static readonly string[] Tools = ["yt-dlp", "ffmpeg"];
    public sealed record State(string Status, string? Version, string? Error, DateTime At);
    public static string Key(string tool) => "tools:" + tool;

    public static async Task Probe(FeedDb db, CancellationToken ct)
    {
        foreach (var tool in Tools) await db.Put(Key(tool), JsonSerializer.Serialize(await Check(tool, ct), InstanceValidation.Json), ct);
    }
    public static async Task<State?> Read(FeedDb db, string tool, CancellationToken ct = default)
        => await db.Get(Key(tool), ct) is { } json ? JsonSerializer.Deserialize<State>(json, InstanceValidation.Json) : null;
    // Unchecked counts as available: a run that has not probed yet must not skip work on a guess.
    public static async Task<bool> Available(FeedDb db, string tool, CancellationToken ct = default)
        => await Read(db, tool, ct) is not { Status: not "ok" };
    public static async Task<string> Line(FeedDb db, CancellationToken ct = default)
    {
        var parts = new List<string>();
        foreach (var tool in Tools)
            if (await Read(db, tool, ct) is { } s)
                parts.Add(s.Status == "ok" ? $"{tool} {s.Version}" : $"{tool} {s.Status}{(s.Error is null ? "" : " (" + s.Error + ")")}, checked {s.At:u}");
        return parts.Count == 0 ? "tools: not checked yet (checked at the start of each processing run)" : "tools: " + string.Join(", ", parts);
    }

    // One version call with a short timeout. ffmpeg takes a single dash; the others take two.
    public static async Task<State> Check(string tool, CancellationToken ct)
    {
        var start = new ProcessStartInfo(tool) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(tool == "ffmpeg" ? "-version" : "--version");
        try
        {
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
                var first = (await stdout).Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
                if (process.ExitCode != 0) return new("error", null, Trim($"exit {process.ExitCode}: {(await stderr).Trim()}"), Clock.Now);
                if (first.StartsWith(tool + " version ", StringComparison.OrdinalIgnoreCase)) first = first[(tool.Length + 9)..];
                return new("ok", Trim(first.Split(' ', 2)[0]), null, Clock.Now);
            }
            finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } }
        }
        catch (Win32Exception) { return new("missing", null, null, Clock.Now); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return new("error", null, "no answer within 10 seconds", Clock.Now); }
    }
    static string Trim(string s) => s.Length > 80 ? s[..80] : s;
}
