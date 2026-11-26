using Feed.Core.Domain;
using System.Security.Cryptography;
using System.Text;
namespace Feed.Core.Infrastructure;

public sealed class InstancePaths
{
    public string Root { get; }
    public string Database => Path.Combine(Root, "feed.db");
    public InstancePaths(string? root = null) => Root = Path.GetFullPath(root ?? Environment.GetEnvironmentVariable("FEED_DATA") ?? "data");
    public string Get(params string[] segments) => Path.Combine([Root, .. segments]);
    public void Create() { Directory.CreateDirectory(Root); foreach (var d in new[] { "profiles", "media", "raw", "logs", "locks", "backgrounds" }) Directory.CreateDirectory(Get(d)); }
    public string? SafeFile(string folder, string relative)
    {
        var root = Get(folder) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full)) return null;
        for (FileSystemInfo? item = new FileInfo(full); item is not null && item.FullName != root.TrimEnd(Path.DirectorySeparatorChar); item = Directory.GetParent(item.FullName))
            if (item.LinkTarget is not null) return null;
        return full;
    }
    public static void AtomicWrite(string path, string text) { Directory.CreateDirectory(Path.GetDirectoryName(path)!); var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp"; File.WriteAllText(tmp, text); File.Move(tmp, path, true); }
}
public sealed class InstanceFiles
{
    static readonly string[] Names = ["config.json", "taxonomy.json", "preferences.md"];
    readonly InstancePaths paths; readonly string[] filePaths; readonly object gate = new(); byte[]? signature; InstanceSnapshot current;
    public string? Error { get; private set; }
    public InstanceFiles(InstancePaths paths)
    {
        this.paths = paths;
        filePaths = Names.Select(n => paths.Get(n)).ToArray();
        current = Read(out signature);
    }
    public InstanceSnapshot Current => Volatile.Read(ref current);
    InstanceSnapshot Read(out byte[] hash)
    {
        // Hash raw file bytes first so unchanged refreshes avoid UTF-8 decoding, string concatenation and re-encoding
        // while still detecting sub-tick edits reliably across any filesystem timestamp granularity.
        var raw = new byte[]?[3];
        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> header = stackalloc byte[5];
        for (int i = 0; i < 3; i++)
        {
            if (File.Exists(filePaths[i]))
            {
                var bytes = File.ReadAllBytes(filePaths[i]);
                raw[i] = bytes;
                header[0] = 1;
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header[1..], bytes.Length);
                hasher.AppendData(header);
                hasher.AppendData(bytes);
            }
            else
            {
                header.Clear();
                hasher.AppendData(header);
            }
        }
        hash = hasher.GetHashAndReset();
        if (signature is not null && current is not null && hash.AsSpan().SequenceEqual(signature)) return current;
        static string? Decode(byte[]? bytes) => bytes is null ? null : Encoding.UTF8.GetString(bytes.AsSpan(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0));
        var c = InstanceValidation.Parse<FeedConfig>(Decode(raw[0])); var t = InstanceValidation.Parse<Taxonomy>(Decode(raw[1]));
        InstanceValidation.Validate(c, t); return new(c, t, Preferences.Parse(Decode(raw[2]) ?? ""));
    }
    public InstanceSnapshot Refresh()
    {
        lock (gate)
        {
            try { var next = Read(out var hash); if (signature is null || !hash.AsSpan().SequenceEqual(signature)) { Volatile.Write(ref current, next); signature = hash; } Error = null; }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { Error = e.Message; }
            return current;
        }
    }
}
