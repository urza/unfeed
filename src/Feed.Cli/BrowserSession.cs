using System.Diagnostics;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Playwright;
namespace Feed.Cli;

public sealed class BrowserSession : IAsyncDisposable
{
    readonly InstancePaths paths; readonly string platform; readonly string stored; readonly string active; readonly IPlaywright playwright; readonly ResourceLock ownership; Process? display;
    public IBrowserContext Context { get; }
    public IPage Page { get; }
    bool export;
    BrowserSession(InstancePaths paths, string platform, string stored, string active, IPlaywright pw, IBrowserContext context, IPage page, ResourceLock ownership, Process? display) { this.paths = paths; this.platform = platform; this.stored = stored; this.active = active; playwright = pw; Context = context; Page = page; this.ownership = ownership; this.display = display; }
    public static async Task<BrowserSession> Open(InstancePaths paths, string platform, FeedConfig config, CancellationToken ct)
    {
        var ownership = ResourceLock.Try(paths, platform) ?? throw new Feed.Core.Application.ResourceBusyException("platform busy");
        IPlaywright? pw = null; Process? display = null;
        var displayName = Environment.GetEnvironmentVariable("DISPLAY");
        try
        {
            if (OperatingSystem.IsLinux() && string.IsNullOrEmpty(displayName))
            {
                displayName = config.Browser.Display;
                if (!await DisplayReady(displayName, ct))
                {
                    var start = new ProcessStartInfo("setsid") { UseShellExecute = false };
                    start.Environment["FEED_XVFB_LOG"] = paths.Get("logs", "xvfb.log");
                    foreach (var arg in new[] { "bash", "-c", "exec Xvfb \"$@\" >>\"$FEED_XVFB_LOG\" 2>&1", "feed-display" }) start.ArgumentList.Add(arg);
                    foreach (var arg in new[] { displayName, "-screen", "0", "1600x1000x24", "-nolisten", "tcp" }) start.ArgumentList.Add(arg);
                    display = Process.Start(start) ?? throw new IOException("Xvfb did not start");
                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!await DisplayReady(displayName, ct))
                    {
                        if (DateTime.UtcNow >= deadline) throw new IOException("Xvfb failed to become ready; no headless fallback");
                        if (display.HasExited) { if (await DisplayReady(displayName, ct)) break; throw new IOException("Xvfb failed to become ready; no headless fallback"); }
                        await Task.Delay(100, ct);
                    }
                    // The display is shared by platform workers and noVNC. Keep it until an explicit helper stop.
                    if (!display.HasExited) InstancePaths.AtomicWrite(paths.Get("locks", "xvfb.pid"), display.Id.ToString());
                    display.Dispose(); display = null;
                }
            }
            var stored = paths.Get("profiles", platform); Directory.CreateDirectory(stored);
            var stageRoot = config.Browser.StageDir ?? Environment.GetEnvironmentVariable("FEED_STAGE_DIR") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "feed", "profiles");
            // Instance hash prevents collisions between independent instances on the same host.
            var active = config.Browser.StageProfiles ? Path.Combine(stageRoot, Feed.Core.Application.Prompts.Sha(paths.Root)[..12], platform) : stored;
            if (active != stored) CopyProfile(stored, active);
            foreach (var f in Directory.EnumerateFiles(active, "Singleton*")) File.Delete(f);
            NotificationPreferences(active, platform);
            pw = await Playwright.CreateAsync();
            var locale = (Environment.GetEnvironmentVariable("LC_ALL") ?? Environment.GetEnvironmentVariable("LANG") ?? "en_US").Split('.')[0].Replace('_', '-'); if (locale is "C" or "POSIX") locale = "en-US";
            var context = await pw.Chromium.LaunchPersistentContextAsync(active, new() { Headless = false, Env = displayName is null ? null : new Dictionary<string, string> { ["DISPLAY"] = displayName }, ViewportSize = new() { Width = 1280, Height = 900 }, Locale = locale, TimezoneId = config.Zone.Id, ChromiumSandbox = !(config.Browser.NoSandbox || Environment.GetEnvironmentVariable("FEED_NO_SANDBOX") == "1") });
            var page = context.Pages.FirstOrDefault() ?? await context.NewPageAsync();
            return new(paths, platform, stored, active, pw, context, page, ownership, display);
        }
        catch { pw?.Dispose(); if (display is { HasExited: false }) display.Kill(); display?.Dispose(); ownership.Dispose(); throw; }
    }
    static async Task<bool> DisplayReady(string name, CancellationToken ct)
    {
        if (!name.StartsWith(':')) return false;
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try { await socket.ConnectAsync(new UnixDomainSocketEndPoint("/tmp/.X11-unix/X" + name[1..].Split('.')[0]), ct); return true; }
        catch (SocketException) { return false; }
    }
    static readonly string[] Excluded = ["Cache", "Code Cache", "GPUCache", "DawnCache", "DawnGraphiteCache", "DawnWebGPUCache", "ShaderCache", "GrShaderCache", "CacheStorage", "ScriptCache", "lockfile", "BrowserMetrics", "Crashpad", "component_crx_cache", "optimization_guide_model_store", "Safe Browsing", "segmentation_platform", "cookies.txt"];
    public static void CopyProfile(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry); if (Excluded.Contains(name) || name.StartsWith("Singleton") || new FileInfo(entry).LinkTarget is not null) continue;
            var target = Path.Combine(destination, name); if (Directory.Exists(entry)) CopyProfile(entry, target); else File.Copy(entry, target, true);
        }
    }
    public static void NotificationPreferences(string directory, string platform)
    {
        var file = Path.Combine(directory, "Default", "Preferences");
        var root = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file))!.AsObject() : new JsonObject(); var profile = root["profile"] as JsonObject ?? new(); root["profile"] = profile;
        var settings = profile["content_settings"] as JsonObject ?? new(); profile["content_settings"] = settings;
        var exceptions = settings["exceptions"] as JsonObject ?? new(); settings["exceptions"] = exceptions;
        var notifications = exceptions["notifications"] as JsonObject ?? new(); exceptions["notifications"] = notifications;
        foreach (var host in platform == "facebook" ? new[] { "facebook.com", "fb.com" } : ["instagram.com"]) { var key = $"https://www.{host}:443,*"; if (notifications[key] is null) notifications[key] = new JsonObject { ["setting"] = 2 }; }
        InstancePaths.AtomicWrite(file, root.ToJsonString());
    }
    public void ExportCookiesOnClose() => export = true;
    public async ValueTask DisposeAsync()
    {
        string? cookies = null;
        try
        {
            if (export) cookies = "# Netscape HTTP Cookie File\n" + string.Join('\n', (await Context.CookiesAsync()).Select(c => $"{c.Domain}\t{(c.Domain.StartsWith('.') ? "TRUE" : "FALSE")}\t{c.Path}\t{(c.Secure ? "TRUE" : "FALSE")}\t{Math.Max(0, (long)c.Expires)}\t{c.Name}\t{c.Value}")) + "\n";
        }
        finally
        {
            try { await Context.CloseAsync(); if (active != stored) { try { CopyProfile(active, stored); } catch (IOException e) { Console.Error.WriteLine("WARNING profile sync-back: " + e.Message); } } if (cookies is not null) InstancePaths.AtomicWrite(paths.Get("profiles", platform, "cookies.txt"), cookies); }
            finally { playwright.Dispose(); ownership.Dispose(); if (display is { HasExited: false }) display.Kill(); display?.Dispose(); }
        }
    }
}
