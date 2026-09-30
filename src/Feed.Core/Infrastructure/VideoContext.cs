using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Feed.Core.Application;
using Feed.Core.Domain;

namespace Feed.Core.Infrastructure;

public sealed record VideoSource(string Url, string Relation, string? CookiePlatform);
public sealed record VideoChapter(double StartSeconds, string Title);
public sealed record VideoCaptions(string Status, string? Language = null, bool Automatic = false, string? Text = null, bool Truncated = false, string? Error = null);
public sealed record VideoContext(string Url, string Relation, string Status, string? Title = null, string? Description = null,
    string? Uploader = null, double? DurationSeconds = null, VideoChapter[]? Chapters = null, VideoCaptions? Captions = null, string? Error = null);
public interface IVideoContextProvider
{
    Task<VideoContext> Get(VideoSource source, VideoContextConfig config, CancellationToken ct);
}
public interface IVideoMetadataTool
{
    Task<string> Extract(VideoSource source, string? cookies, CancellationToken ct);
}

// yt-dlp is an extractor only here: never download media, execute local configuration, or open a browser profile.
public sealed class YtDlpMetadataTool : IVideoMetadataTool
{
    public async Task<string> Extract(VideoSource source, string? cookies, CancellationToken ct)
    {
        var start = new ProcessStartInfo("yt-dlp") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "--ignore-config", "--no-cache-dir", "--no-playlist", "--playlist-items", "1", "--skip-download", "--dump-single-json", "--no-progress", "--no-warnings", "--socket-timeout", "10", "--retries", "0", "--extractor-retries", "0" }) start.ArgumentList.Add(a);
        if (cookies is not null) { start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookies); }
        start.ArgumentList.Add("--"); start.ArgumentList.Add(source.Url);
        using var process = Process.Start(start) ?? throw new IOException("extractor could not start");
        using var stop = ct.Register(() => Kill(process));
        async Task<byte[]> Read(Stream stream, int max)
        {
            try { return await VideoContextProvider.ReadBounded(stream, max, ct); }
            catch { Kill(process); throw; }
        }
        var stdout = Read(process.StandardOutput.BaseStream, 4_000_000);
        var stderr = Read(process.StandardError.BaseStream, 64_000);
        try
        {
            await process.WaitForExitAsync(ct); await Task.WhenAll(stdout, stderr);
            ct.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("video metadata extractor failed");
            return Encoding.UTF8.GetString(await stdout);
        }
        finally
        {
            Kill(process); await process.WaitForExitAsync(CancellationToken.None);
            try { await Task.WhenAll(stdout, stderr); } catch { /* Both pipe readers are observed, including cancellation/size failures. */ }
        }
    }
    static void Kill(Process process) { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } }
}

public sealed class VideoContextProvider : IVideoContextProvider, IDisposable
{
    public const int Version = 1;
    public const int CaptionLimit = 8000;
    readonly InstancePaths paths;
    readonly HttpClient http;
    readonly bool ownsHttp;
    readonly IVideoMetadataTool tool;
    readonly SemaphoreSlim slots = new(2);
    readonly SemaphoreSlim cacheGate = new(1);
    sealed record CacheEntry(DateTime ExpiresAt, VideoContext Context);
    sealed record Track(string Language, bool Automatic, string Url, string Format);
    public VideoContextProvider(InstancePaths paths, HttpClient? http = null, IVideoMetadataTool? tool = null)
    {
        this.paths = paths; this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
        ownsHttp = http is null; this.tool = tool ?? new YtDlpMetadataTool();
    }
    public void Dispose() { slots.Dispose(); cacheGate.Dispose(); if (ownsHttp) http.Dispose(); }
    static bool Host(Uri u, string host) => u.Host.Equals(host, StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase);
    static bool Web(string? url, out Uri uri) => Uri.TryCreate(url, UriKind.Absolute, out uri!) && uri.Scheme is "http" or "https" && uri.UserInfo.Length == 0 && uri.IsDefaultPort;
    public static VideoSource? Source(Post p, IReadOnlyList<Media> media)
    {
        // Shared destinations only: never scan arbitrary caption links or treat a thumbnail as proof of video.
        if (Web(p.SharedUrl, out var shared))
        {
            var path = shared.AbsolutePath;
            if (Host(shared, "youtu.be") && path.Trim('/').Length > 0
                || (Host(shared, "youtube.com") || Host(shared, "youtube-nocookie.com")) && (path == "/watch" && Regex.IsMatch(shared.Query, @"(?:[?&])v=[\w-]+") || Regex.IsMatch(path, @"^/(shorts|embed|live)/[\w-]+"))
                || Host(shared, "vimeo.com") && Regex.IsMatch(path, @"/(?:video/)?\d+(?:/|$)"))
                return new(shared.AbsoluteUri, "shared", null);
            if (Host(shared, "facebook.com") && (Regex.IsMatch(path, @"/(videos|reel|share/v|share/r)/[^/]+") || path.TrimEnd('/') == "/watch" && Regex.IsMatch(shared.Query, @"(?:[?&])v=\d+")))
                return new(shared.AbsoluteUri, "shared", "facebook");
            if (Host(shared, "instagram.com") && Regex.IsMatch(path, @"^/reels?/[\w-]+"))
                return new(shared.AbsoluteUri, "shared", "instagram");
        }
        if (media.Any(m => m.IsCurrent && m.Kind == "video") && Web(p.Permalink, out var native) && Platforms.All.Contains(p.Platform) && Host(native, p.Platform + ".com"))
            return new(native.AbsoluteUri, "post", p.Platform);
        return null;
    }
    public async Task<VideoContext> Get(VideoSource source, VideoContextConfig config, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var key = Prompts.Sha(JsonSerializer.Serialize(new { Version, source, config.CaptionLanguages }));
        var cacheRoot = paths.Get("video-context"); var cachePath = Path.Combine(cacheRoot, key + ".json");
        if (ReadCache(cachePath) is { } cached) return cached;
        await slots.WaitAsync(ct);
        try
        {
            if (ReadCache(cachePath) is { } again) return again;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var token = timeout.Token; string? cookieCopy = null;
            VideoContext result;
            try
            {
                if (source.CookiePlatform is { } platform && Platforms.All.Contains(platform) && File.Exists(paths.Get("profiles", platform, "cookies.txt")))
                {
                    var tempRoot = paths.Get("video-context", "private"); Directory.CreateDirectory(tempRoot);
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(tempRoot, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                    cookieCopy = Path.Combine(tempRoot, Guid.NewGuid().ToString("N") + ".cookies");
                    File.Copy(paths.Get("profiles", platform, "cookies.txt"), cookieCopy);
                }
                using var doc = JsonDocument.Parse(await tool.Extract(source, cookieCopy, token));
                var e = doc.RootElement;
                if (e.ValueKind != JsonValueKind.Object || PayloadParser.Get(e, "_type") is "playlist" or "multi_video" || PayloadParser.At(e, "entries").ValueKind == JsonValueKind.Array)
                    throw new FormatException("not a single video");
                if (VideoIdentity(source.Url) is { } expected && VideoIdentity(PayloadParser.Get(e, "webpage_url")) is { } actual && expected != actual)
                    throw new FormatException("video identity changed during extraction");
                var title = Cut(PayloadParser.Get(e, "title"), 500);
                if (title is null && PayloadParser.Get(e, "id") is null) throw new FormatException("missing video identity");
                var chapters = PayloadParser.Array(PayloadParser.At(e, "chapters")).Take(12)
                    .Where(x => Number(x, "start_time") is >= 0 && PayloadParser.Get(x, "title") is not null)
                    .Select(x => new VideoChapter(Number(x, "start_time")!.Value, Cut(PayloadParser.Get(x, "title"), 160)!)).ToArray();
                result = new(source.Url, source.Relation, "available", title, Cut(PayloadParser.Get(e, "description"), 3000),
                    Cut(PayloadParser.Get(e, "uploader", "channel", "creator"), 200), Number(e, "duration"), chapters, new("not_available"));
                var track = SelectTrack(e, config);
                if (track is not null)
                {
                    try
                    {
                        // No browser cookies or extractor-provided headers go to a subtitle endpoint.
                        using var response = await http.GetAsync(track.Url, HttpCompletionOption.ResponseHeadersRead, token);
                        response.EnsureSuccessStatusCode();
                        await using var stream = await response.Content.ReadAsStreamAsync(token);
                        var body = Encoding.UTF8.GetString(await ReadBounded(stream, 1_000_000, token));
                        var captions = ParseCaptions(body, track.Format, track.Language, track.Automatic);
                        result = result with { Captions = captions };
                    }
                    catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
                    { result = result with { Captions = new("unavailable", track.Language, track.Automatic, Error: Failure(ex)) }; }
                }
            }
            catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
            { result = new(source.Url, source.Relation, "unavailable", Captions: new("unavailable"), Error: Failure(ex)); }
            finally { if (cookieCopy is not null) File.Delete(cookieCopy); }
            ct.ThrowIfCancellationRequested();
            // Cache failures briefly; rejudgment remains explicit after an otherwise successful verdict.
            var ttl = result.Status == "available" && result.Captions?.Status is not "unavailable" ? TimeSpan.FromDays(7) : TimeSpan.FromMinutes(30);
            await cacheGate.WaitAsync(ct);
            try
            {
                Directory.CreateDirectory(cacheRoot);
                InstancePaths.AtomicWrite(cachePath, JsonSerializer.Serialize(new CacheEntry(Clock.Now + ttl, result), InstanceValidation.Json));
                foreach (var stale in new DirectoryInfo(cacheRoot).EnumerateFiles("*.json").OrderByDescending(f => f.LastWriteTimeUtc).Skip(256)) stale.Delete();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Cache failure must not block judgment. */ }
            finally { cacheGate.Release(); }
            return result;
        }
        finally { slots.Release(); }
    }
    static bool Recoverable(Exception e) => e is IOException or UnauthorizedAccessException or HttpRequestException or OperationCanceledException or JsonException or FormatException or System.ComponentModel.Win32Exception;
    static string Failure(Exception e) => e switch { OperationCanceledException => "timeout", System.ComponentModel.Win32Exception => "tool_missing", JsonException or FormatException => "invalid_response", _ => "retrieval_failed" };
    static string? VideoIdentity(string? url)
    {
        if (!Web(url, out var u)) return null;
        if (Host(u, "youtu.be")) return "youtube:" + u.AbsolutePath.Trim('/');
        if (Host(u, "youtube.com") || Host(u, "youtube-nocookie.com"))
        {
            var m = Regex.Match(u.Query, @"(?:[?&])v=([\w-]+)");
            if (!m.Success) m = Regex.Match(u.AbsolutePath, @"^/(?:shorts|embed|live)/([\w-]+)");
            return m.Success ? "youtube:" + m.Groups[1].Value : null;
        }
        if (Host(u, "instagram.com")) { var m = Regex.Match(u.AbsolutePath, @"^/(?:p|reels?)/([\w-]+)"); return m.Success ? "instagram:" + m.Groups[1].Value : null; }
        if (Host(u, "vimeo.com")) { var m = Regex.Match(u.AbsolutePath, @"/(\d+)(?:/|$)"); return m.Success ? "vimeo:" + m.Groups[1].Value : null; }
        if (Host(u, "facebook.com"))
        {
            var m = Regex.Match(u.AbsolutePath, @"/(?:videos|reel)/(\d+)");
            if (!m.Success) m = Regex.Match(u.Query, @"(?:[?&])v=(\d+)");
            return m.Success ? "facebook:" + m.Groups[1].Value : null;
        }
        return null;
    }
    static VideoContext? ReadCache(string path)
    {
        try { if (!File.Exists(path) || new FileInfo(path).Length > 128_000) return null; var c = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path), InstanceValidation.Json); return c?.ExpiresAt > Clock.Now ? c.Context : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
    static string? Cut(string? text, int max) => text is null ? null : text[..Math.Min(text.Length, max)];
    static double? Number(JsonElement e, string key) => PayloadParser.At(e, key).TryGetDoubleSafe() is { } n && double.IsFinite(n) && n >= 0 ? n : null;
    static Track? SelectTrack(JsonElement e, VideoContextConfig config)
    {
        var languages = config.CaptionLanguages.ToArray();
        foreach (var automatic in new[] { false, true })
        {
            var tracks = PayloadParser.At(e, automatic ? "automatic_captions" : "subtitles");
            if (tracks.ValueKind != JsonValueKind.Object) continue;
            var candidates = tracks.EnumerateObject().Where(p => p.Name != "live_chat").ToArray();
            // YouTube advertises machine translations alongside the original automatic track. Do not silently translate.
            if (automatic && candidates.Any(p => p.Name.EndsWith("-orig", StringComparison.Ordinal))) candidates = candidates.Where(p => p.Name.EndsWith("-orig", StringComparison.Ordinal)).ToArray();
            foreach (var language in languages)
            foreach (var candidate in candidates.Where(p => p.Name.Equals(language, StringComparison.OrdinalIgnoreCase) || p.Name.StartsWith(language + "-", StringComparison.OrdinalIgnoreCase)).OrderBy(p => p.Name, StringComparer.Ordinal))
            foreach (var format in new[] { "json3", "vtt", "srt" })
            {
                var track = PayloadParser.Array(candidate.Value).FirstOrDefault(t => PayloadParser.Get(t, "ext") == format && CaptionUrl(PayloadParser.Get(t, "url")));
                if (track.ValueKind != JsonValueKind.Undefined) return new(candidate.Name, automatic, PayloadParser.Get(track, "url")!, format);
            }
        }
        return null;
    }
    static bool CaptionUrl(string? text)
    {
        if (!Web(text, out var uri) || uri.Scheme != "https" || uri.IsLoopback || !uri.Host.Contains('.') || IPAddress.TryParse(uri.Host, out _)) return false;
        return !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase);
    }
    public static VideoCaptions ParseCaptions(string body, string format, string language, bool automatic)
    {
        var cues = new List<string>();
        if (format == "json3")
        {
            using var doc = JsonDocument.Parse(body);
            foreach (var cue in PayloadParser.Array(PayloadParser.At(doc.RootElement, "events")))
                cues.Add(string.Concat(PayloadParser.Array(PayloadParser.At(cue, "segs")).Select(s => PayloadParser.Get(s, "utf8"))));
        }
        else if (format is "vtt" or "srt")
        {
            foreach (var block in Regex.Split(body.Replace("\r", ""), @"\n\s*\n"))
            {
                var lines = block.Split('\n'); var timing = Array.FindIndex(lines, l => l.Contains("-->"));
                if (timing < 0 || lines[0].StartsWith("NOTE", StringComparison.Ordinal) || lines[0] is "STYLE" or "REGION") continue;
                cues.Add(string.Join(' ', lines.Skip(timing + 1)));
            }
        }
        else throw new FormatException("unsupported captions");
        var output = new StringBuilder(); string previous = ""; bool truncated = false;
        foreach (var cue in cues)
        {
            var clean = Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(cue, "<[^>]*>", "")), @"\s+", " ").Trim();
            if (clean.Length == 0 || clean == previous) continue;
            // Remove repeated rolling subtitle prefixes without globally removing genuinely repeated speech.
            var addition = clean; var overlap = Math.Min(previous.Length, clean.Length);
            for (; overlap > 0; overlap--) if ((previous.Length == overlap || previous[previous.Length - overlap - 1] == ' ') && previous.EndsWith(clean[..overlap], StringComparison.Ordinal) && (overlap == clean.Length || clean[overlap] == ' ')) { addition = clean[overlap..].TrimStart(); break; }
            previous = clean; if (addition.Length == 0) continue;
            if (output.Length >= CaptionLimit) { truncated = true; break; }
            if (output.Length > 0) output.Append(' ');
            var remaining = CaptionLimit - output.Length;
            if (addition.Length > remaining) { output.Append(addition.AsSpan(0, Math.Max(0, remaining))); truncated = true; break; }
            output.Append(addition);
        }
        return output.Length == 0 ? new("unavailable", language, automatic) : new("available", language, automatic, output.ToString(), truncated);
    }
    public static async Task<byte[]> ReadBounded(Stream stream, int max, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0) { if (buffer.Length + read > max) throw new IOException("video context response exceeds size limit"); buffer.Write(chunk, 0, read); }
        return buffer.ToArray();
    }
}
file static class JsonNumber
{
    public static double? TryGetDoubleSafe(this JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var n) ? n : null;
}
