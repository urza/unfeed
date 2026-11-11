using System.Diagnostics;
namespace Feed.Core.Infrastructure;

public static class ToolProcess
{
    // Runs a command-line tool to completion with both streams captured as text. On timeout or cancellation the
    // process tree is killed and the OperationCanceledException reaches the caller, which decides what it means.
    // A missing executable surfaces as Win32Exception from Process.Start, as before.
    public static async Task<(int ExitCode, string Stdout, string Stderr)> Run(ProcessStartInfo start, TimeSpan limit, CancellationToken ct)
    {
        start.UseShellExecute = false; start.RedirectStandardOutput = true; start.RedirectStandardError = true;
        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(limit);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token); var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try { await process.WaitForExitAsync(timeout.Token); return (process.ExitCode, await stdout, await stderr); }
        finally { if (!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); try { await Task.WhenAll(stdout, stderr); } catch (OperationCanceledException) { } }
    }
}
