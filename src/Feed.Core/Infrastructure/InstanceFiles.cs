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
    readonly InstancePaths paths; readonly object gate = new(); string signature = ""; InstanceSnapshot current;
    public string? Error { get; private set; }
    public InstanceFiles(InstancePaths paths) { this.paths = paths; current = Read(out signature); }
    public InstanceSnapshot Current => Volatile.Read(ref current);
    InstanceSnapshot Read(out string hash)
    {
        var texts = new[] { "config.json", "taxonomy.json", "preferences.md" }.Select(n => File.Exists(paths.Get(n)) ? File.ReadAllText(paths.Get(n)) : null).ToArray();
        hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\0', texts))));
        if (hash == signature && current is not null) return current;
        var c = InstanceValidation.Parse<FeedConfig>(texts[0]); var t = InstanceValidation.Parse<Taxonomy>(texts[1]);
        InstanceValidation.Validate(c, t); return new(c, t, Preferences.Parse(texts[2] ?? ""));
    }
    public InstanceSnapshot Refresh()
    {
        lock (gate)
        {
            try { var next = Read(out var hash); if (hash != signature) { Volatile.Write(ref current, next); signature = hash; } Error = null; }
            catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or IOException or UnauthorizedAccessException) { Error = e.Message; }
            return current;
        }
    }
}
