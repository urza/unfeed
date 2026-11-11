using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
namespace Feed.Tests;
// Starts the Debug build of Feed.Web for one TestInstance and waits until /healthz answers.
// The browser tests run src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll, so build that project first.
public sealed class WebHost : IAsyncDisposable
{
    readonly Process process; readonly Task<string> stdout, stderr;
    public HttpClient Http { get; }
    public Uri BaseAddress => Http.BaseAddress!;
    WebHost(Process process, int port)
    {
        this.process = process;
        // Read both streams from the start: a full pipe buffer would block the host.
        stdout = process.StandardOutput.ReadToEndAsync(); stderr = process.StandardError.ReadToEndAsync();
        Http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{port}/") };
    }
    public static async Task<WebHost> Start(TestInstance instance, Action<ProcessStartInfo>? configure = null)
    {
        // The port is free when the listener stops; the host claims it a moment later.
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Feed.slnx"))) root = root.Parent;
        if (root is null) throw new DirectoryNotFoundException("Feed.slnx not found above " + AppContext.BaseDirectory);
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { WorkingDirectory = root.FullName, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { Path.Combine(root.FullName, "src/Feed.Web/bin/Debug/net10.0/Feed.Web.dll"), "--data", instance.Paths.Root, "--urls", $"http://127.0.0.1:{port}" }) start.ArgumentList.Add(arg);
        configure?.Invoke(start);
        var host = new WebHost(Process.Start(start)!, port);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            while (true)
            {
                if (host.process.HasExited) throw new InvalidOperationException("Web host exited: " + await host.stderr);
                try { if ((await host.Http.GetAsync("/healthz", timeout.Token)).IsSuccessStatusCode) break; }
                catch (HttpRequestException) { }
                await Task.Delay(100, timeout.Token);
            }
            return host;
        }
        // A failed start still owns a live process: kill it before the exception leaves.
        catch { await host.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited) process.Kill(true);
        await process.WaitForExitAsync(); await stdout; await stderr;
        process.Dispose(); Http.Dispose();
    }
}
