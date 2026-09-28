using System.Text.Json;
using System.Text.RegularExpressions;
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
            // Prefer the pinned/current filename when consolidating old regional copies.
            foreach (var group in images.Where(i => i.Source == "bing").GroupBy(i => i.Etag).ToArray())
            {
                var removedContent = Removed("sha256:" + group.Key.Trim('"'));
                var copies = group.OrderByDescending(i => i.Name == pin).ThenByDescending(i => i.Name == snapshot.Selected).ThenBy(i => i.Name, StringComparer.Ordinal).ToArray();
                foreach (var duplicate in copies.Skip(removedContent ? 0 : 1))
                {
                    RememberRemoved(duplicate.Name);
                    File.Delete(duplicate.Path);
                    images.Remove(duplicate);
                }
            }
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
                var image = snapshot.Images.Single(i => i.Name == name); if (image.Source == "bing") { RememberRemoved(image.Name, "sha256:" + image.Etag.Trim('"')); }
                File.Delete(image.Path); if (snapshot.Pinned == name && File.Exists(pin)) File.Delete(pin);
            }
            else return false;
            Scan(action == "next"); return true;
        }
    }
    public async Task<string?> Upload(IFormFileCollection uploads, CancellationToken ct)
    {
        if (uploads.Count is < 1 or > 10) return "Choose between 1 and 10 pictures.";
        if (uploads.Any(f => f.Length is <= 0 or > 10 * 1024 * 1024) || uploads.Sum(f => f.Length) > 20 * 1024 * 1024)
            return "Use nonempty files up to 10 MB each and 20 MB total.";
        var validated = new List<(string Name, byte[] Bytes)>();
        foreach (var upload in uploads)
        {
            var original = Path.GetFileName(upload.FileName.Replace('\\', '/'));
            var extension = Path.GetExtension(original).ToLowerInvariant();
            var expected = extension switch { ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".webp" => "image/webp", _ => null };
            if (expected is null) return "Only JPG, JPEG, PNG and WebP pictures are supported. No files were saved.";
            using var output = new MemoryStream();
            await upload.CopyToAsync(output, ct);
            var bytes = output.ToArray();
            var info = ImageHeaders.Read(bytes);
            if (info?.Mime != expected || info.Width is not > 0 || info.Height is not > 0)
                return "A file does not have valid image headers matching its extension. No files were saved.";
            var stem = Regex.Replace(Path.GetFileNameWithoutExtension(original), @"[^a-zA-Z0-9_-]", "-");
            if (stem.Length > 60) stem = stem[..60];
            validated.Add(($"{stem}-{Guid.NewGuid():N}{extension}", bytes));
        }
        lock (gate)
        {
            var directory = paths.Get("backgrounds", "local");
            Directory.CreateDirectory(directory);
            foreach (var (name, bytes) in validated)
            {
                ct.ThrowIfCancellationRequested();
                var full = Path.Combine(directory, name);
                var temp = full + ".tmp";
                try { File.WriteAllBytes(temp, bytes); File.Move(temp, full, false); }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
            Scan();
        }
        return null;
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
                    var urlbase = item.GetProperty("urlbase").GetString(); if (urlbase is null || !urlbase.StartsWith("/th?id=OHR.", StringComparison.Ordinal) || urlbase.Contains("..")) continue;
                    var identity = Regex.Replace(urlbase, @"_[A-Z]{2}-[A-Z]{2}\d+(?=$|&)", "", RegexOptions.CultureInvariant);
                    if (!seen.Add(identity)) continue;
                    var legacyName = "bing-" + Prompts.Sha(urlbase)[..24] + ".jpg";
                    var name = "bing-" + Prompts.Sha(identity)[..24] + ".jpg"; var full = paths.Get("backgrounds", name);
                    lock (gate) { if (File.Exists(full) || Removed(name) || Removed(legacyName)) continue; }
                    using var response = await http.GetAsync("https://www.bing.com" + urlbase + "_1920x1080.jpg", ct); response.EnsureSuccessStatusCode(); if (response.RequestMessage?.RequestUri?.Host is not ("www.bing.com" or "bing.com") || response.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) throw new IOException("Unexpected Bing image origin or content type");
                    var bytes = await response.Content.ReadAsByteArrayAsync(ct); if (ImageHeaders.Read(bytes) is null) throw new IOException("Invalid background image");
                    lock (gate) { if (Removed(name) || Removed(legacyName) || Removed("sha256:" + MediaFiles.Hash(bytes))) continue; var tmp = full + ".tmp"; File.WriteAllBytes(tmp, bytes); File.Move(tmp, full, true); downloaded++; }
                }
            }
            catch (Exception e) when (!ct.IsCancellationRequested) { failed++; log.LogWarning("Bing market {Market}: {Error}", market, e.Message); }
        }
        var outcome = $"{Clock.Now:O}: downloaded={downloaded}, failed markets={failed}";
        InstancePaths.AtomicWrite(stateFile, JsonSerializer.Serialize(new { day, ok = failed == 0, next = Clock.Now.AddMinutes(failed == 0 ? 0 : 10), outcome }));
        lock (gate) Volatile.Write(ref snapshot, snapshot with { Outcome = outcome }); Scan();
    }
    void RememberRemoved(params string[] names)
    {
        var path = paths.Get("backgrounds", "removed.txt");
        var entries = File.Exists(path) ? File.ReadAllLines(path).ToHashSet() : [];
        entries.UnionWith(names);
        InstancePaths.AtomicWrite(path, string.Join('\n', entries));
    }
    bool Removed(string name) { var path = paths.Get("backgrounds", "removed.txt"); return File.Exists(path) && File.ReadAllLines(path).Contains(name); }
}
