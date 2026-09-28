using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using Feed.Core.Domain;
namespace Feed.Core.Infrastructure;

public sealed record ImageInfo(string Mime, int? Width, int? Height);
public static class ImageHeaders
{
    public static ImageInfo? Read(byte[] b)
    {
        try
        {
            if (b.Length >= 24 && b.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return new("image/png", BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16, 4)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20, 4)));
            if (b.Length >= 10 && System.Text.Encoding.ASCII.GetString(b, 0, 3) == "GIF") return new("image/gif", BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(6, 2)), BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(8, 2)));
            if (b.Length >= 30 && System.Text.Encoding.ASCII.GetString(b, 8, 4) == "WEBP")
            {
                string chunk = System.Text.Encoding.ASCII.GetString(b, 12, 4);
                if (chunk == "VP8X") return new("image/webp", 1 + b[24] + (b[25] << 8) + (b[26] << 16), 1 + b[27] + (b[28] << 8) + (b[29] << 16));
                if (chunk == "VP8L") { int bits = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(21, 4)); return new("image/webp", (bits & 0x3fff) + 1, ((bits >> 14) & 0x3fff) + 1); }
                if (chunk == "VP8 ") return new("image/webp", BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26, 2)) & 0x3fff, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28, 2)) & 0x3fff);
                return new("image/webp", null, null);
            }
            if (b.Length >= 4 && b[0] == 255 && b[1] == 216)
            {
                int i = 2; while (i + 8 < b.Length) { if (b[i++] != 255) continue; byte marker = b[i++]; if (marker is 216 or 217) continue; int length = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i, 2)); if (length < 2 || i + length > b.Length) break; if (marker is >= 192 and <= 195 or >= 197 and <= 199 or >= 201 and <= 203 or >= 205 and <= 207) return new("image/jpeg", BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5, 2)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 3, 2))); i += length; }
                return new("image/jpeg", null, null);
            }
        }
        catch (ArgumentException) { }
        return null;
    }
}
public sealed class MediaFiles(InstancePaths paths, HttpClient http)
{
    public static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public async Task<(string? Path, string? Hash, ImageInfo? Info, string? Error)> Image(string relative, string url, bool offline, bool avatar, CancellationToken ct)
    {
        var full = paths.Get(relative); string? tmp = null;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (!File.Exists(full) || new FileInfo(full).Length == 0)
            {
                if (offline) return (null, null, null, "offline: image absent");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(60));
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token); response.EnsureSuccessStatusCode();
                tmp = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await using (var output = File.Create(tmp)) await response.Content.CopyToAsync(output, timeout.Token);
                File.Move(tmp, full, false); tmp = null;
                await Task.Delay(Random.Shared.Next(500, 1501), ct);
            }
            var bytes = await File.ReadAllBytesAsync(full, ct); var info = ImageHeaders.Read(bytes);
            if (!avatar && info is { Width: < 64, Height: < 64 }) { File.Delete(full); return (null, null, null, "glyph below 64px"); }
            return (relative, Hash(bytes), info, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException) { if (ct.IsCancellationRequested) throw; return (null, null, null, e.Message); }
        finally { if (tmp is not null && File.Exists(tmp)) File.Delete(tmp); }
    }
    public static string Extension(string url) { var ext = Uri.TryCreate(url, UriKind.Absolute, out var u) ? Path.GetExtension(u.AbsolutePath).ToLowerInvariant() : ""; return new[] { ".jpg", ".jpeg", ".png", ".gif", ".webp" }.Contains(ext) ? ext : ".jpg"; }
    public async Task<(byte[] Bytes, string Mime, bool Fallback)?> Vision(string relative, CancellationToken ct)
    {
        var full = paths.SafeFile("media", relative.StartsWith("media/") ? relative[6..] : relative); if (full is null || new FileInfo(full).Length is 0 or > 8000000) return null;
        var bytes = await File.ReadAllBytesAsync(full, ct); var info = ImageHeaders.Read(bytes); if (info is null) return null;
        if (info.Width <= 768 && info.Height <= 768) return (bytes, info.Mime, false);
        try
        {
            var start = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in new[] { "-v", "error", "-i", full, "-vf", "scale='if(gte(iw,ih),768,-2)':'if(gte(iw,ih),-2,768)'", "-frames:v", "1", "-q:v", "4", "-f", "image2pipe", "-vcodec", "mjpeg", "pipe:1" }) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            using var cancelProcess = ct.Register(() => { try { if (!p.HasExited) p.Kill(true); } catch (InvalidOperationException) { } });
            using var output = new MemoryStream(); var err = p.StandardError.ReadToEndAsync(ct); await p.StandardOutput.BaseStream.CopyToAsync(output, ct); await p.WaitForExitAsync(ct); await err;
            if (p.ExitCode == 0 && output.Length > 0) return (output.ToArray(), "image/jpeg", false);
        }
        catch (System.ComponentModel.Win32Exception) { }
        return (bytes, info.Mime, true);
    }
}
