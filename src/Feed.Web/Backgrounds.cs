using System.Text.Json;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
namespace Feed.Web;
public sealed record BackgroundImage(string Name, string Path, string Source, string Etag);
public sealed record BackgroundSnapshot(BackgroundImage[] Images, string? Selected, string? Pinned, string? Outcome);
public sealed class Backgrounds(InstancePaths paths, InstanceFiles files, ILogger<Backgrounds> log) : BackgroundService
{
    readonly object gate = new(); BackgroundSnapshot snapshot = new([], null, null, null); DateTime rotated; public BackgroundSnapshot Current => Volatile.Read(ref snapshot);
    readonly string[] markets = ["en-US", "en-GB", "de-DE", "fr-FR", "it-IT", "es-ES", "ja-JP", "en-IN"];
    public void Scan(bool next = false)
    {
        lock (gate)
        {
            var images = new List<BackgroundImage>();
            foreach (var (folder, source) in new[] { ("backgrounds", "bing"), ("backgrounds/local", "local") })
            {
                if (!Directory.Exists(paths.Get(folder))) continue;
                foreach (var file in Directory.EnumerateFiles(paths.Get(folder)).Where(f => new[] { ".jpg", ".jpeg", ".png", ".webp" }.Contains(Path.GetExtension(f).ToLowerInvariant())))
                {
                    if (paths.SafeFile(folder, Path.GetFileName(file)) is null) continue;
                    var name = source == "local" ? "local-" + Path.GetFileName(file) : Path.GetFileName(file);
                    images.Add(new(name, file, source, "\"" + MediaFiles.Hash(File.ReadAllBytes(file)) + "\""));
                }
            }
            var pinPath = paths.Get("backgrounds", "pinned.txt"); var pin = File.Exists(pinPath) ? File.ReadAllText(pinPath).Trim() : null;
            string? outcome = snapshot.Outcome;
            var refreshState = paths.Get("backgrounds", "refresh.json");
            if (outcome is null && File.Exists(refreshState)) try { using var state = JsonDocument.Parse(File.ReadAllText(refreshState)); if (state.RootElement.TryGetProperty("outcome", out var result)) outcome = result.GetString(); } catch (JsonException) { }
            if (!images.Any(i => i.Name == pin)) { if (!string.IsNullOrEmpty(pin)) outcome = "Ignored missing or invalid pin: " + pin; pin = null; }
            var selected = pin ?? snapshot.Selected;
            if (next || !images.Any(i => i.Name == selected) || pin is null && Clock.Now - rotated >= TimeSpan.FromHours(1))
            {
                var choices = images.Where(i => images.Count == 1 || i.Name != selected).ToArray(); selected = choices.Length == 0 ? null : choices[Random.Shared.Next(choices.Length)].Name; rotated = Clock.Now;
            }
            Volatile.Write(ref snapshot, new(images.ToArray(), selected, pin, outcome));
        }
    }
    public bool Action(string action, string? name)
    {
        lock (gate)
        {
            if (action is "pin" or "remove" && !snapshot.Images.Any(i => i.Name == name)) return false;
            var pin = paths.Get("backgrounds", "pinned.txt");
            if (action == "pin") InstancePaths.AtomicWrite(pin, name!);
            else if (action is "unpin" or "next") { if (File.Exists(pin)) File.Delete(pin); }
            else if (action == "remove")
            {
                var image = snapshot.Images.Single(i => i.Name == name); if (image.Source == "bing") { var removed = paths.Get("backgrounds", "removed.txt"); var entries = File.Exists(removed) ? File.ReadAllLines(removed).ToHashSet() : []; entries.Add(image.Name); InstancePaths.AtomicWrite(removed, string.Join('\n', entries)); }
                File.Delete(image.Path); if (snapshot.Pinned == name && File.Exists(pin)) File.Delete(pin);
            }
            else return false;
            Scan(action == "next"); return true;
        }
    }
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) }; Scan();
        while (!ct.IsCancellationRequested)
        {
            try { Scan(); if (files.Refresh().Config.Backgrounds.Source == "bing") await Refresh(http, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception e) { log.LogWarning(e, "Background maintenance failed"); }
            await Task.Delay(TimeSpan.FromMinutes(1), ct);
        }
    }
    public async Task Refresh(HttpClient http, CancellationToken ct)
    {
        var stateFile = paths.Get("backgrounds", "refresh.json"); var day = Clock.Now.ToString("yyyy-MM-dd");
        if (File.Exists(stateFile)) { try { using var state = JsonDocument.Parse(File.ReadAllText(stateFile)); if (state.RootElement.GetProperty("day").GetString() == day && state.RootElement.GetProperty("ok").GetBoolean() || state.RootElement.GetProperty("next").GetDateTime() > Clock.Now) return; } catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { } }
        int downloaded = 0, failed = 0; var seen = new HashSet<string>();
        foreach (var market in markets)
        {
            try
            {
                using var archive = JsonDocument.Parse(await http.GetStringAsync($"https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt={market}", ct));
                foreach (var item in archive.RootElement.GetProperty("images").EnumerateArray())
                {
                    var urlbase = item.GetProperty("urlbase").GetString(); if (urlbase is null || !urlbase.StartsWith("/th?id=OHR.", StringComparison.Ordinal) || urlbase.Contains("..") || !seen.Add(urlbase)) continue;
                    var name = "bing-" + Prompts.Sha(urlbase)[..24] + ".jpg"; var full = paths.Get("backgrounds", name);
                    lock (gate) { if (File.Exists(full) || Removed(name)) continue; }
                    using var response = await http.GetAsync("https://www.bing.com" + urlbase + "_1920x1080.jpg", ct); response.EnsureSuccessStatusCode(); if (response.RequestMessage?.RequestUri?.Host is not ("www.bing.com" or "bing.com") || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) throw new IOException("Unexpected Bing image origin or content type");
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct); if (ImageHeaders.Read(bytes) is null) throw new IOException("Invalid background image");
                    lock (gate) { if (Removed(name)) continue; var tmp = full + ".tmp"; File.WriteAllBytes(tmp, bytes); File.Move(tmp, full, true); downloaded++; }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { failed++; log.LogWarning("Bing market {Market}: {Error}", market, e.Message); }
        }
        var outcome = $"{Clock.Now:O}: downloaded={downloaded}, failed markets={failed}";
        InstancePaths.AtomicWrite(stateFile, JsonSerializer.Serialize(new { day, ok = failed == 0, next = Clock.Now.AddMinutes(failed == 0 ? 0 : 10), outcome }));
        lock (gate) Volatile.Write(ref snapshot, snapshot with { Outcome = outcome }); Scan();
    }
    bool Removed(string name) { var path = paths.Get("backgrounds", "removed.txt"); return File.Exists(path) && File.ReadAllLines(path).Contains(name); }
}
