using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;

namespace Feed.Web;

public sealed record ManagementDocuments(string? Config, string? Taxonomy, string? Preferences)
{
    public string Version => Prompts.Sha(JsonSerializer.Serialize(new[] { Config, Taxonomy, Preferences }));
    public InstanceSnapshot Snapshot()
    {
        var config = InstanceValidation.Parse<FeedConfig>(Config); var taxonomy = InstanceValidation.Parse<Taxonomy>(Taxonomy);
        InstanceValidation.Validate(config, taxonomy);
        return new(config, taxonomy, Feed.Core.Domain.Preferences.Parse(Preferences ?? ""));
    }
    public JsonObject ConfigObject() => ParseObject(Config);
    public JsonObject TaxonomyObject() => ParseObject(Taxonomy);
    static JsonObject ParseObject(string? text) => text is null ? new() : JsonNode.Parse(text, documentOptions: new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })!.AsObject();
}

// File edits use a separate lock; workers keep using immutable validated snapshots.
public sealed class ManagementFiles(InstancePaths paths)
{
    public ManagementDocuments Read() => new(Read("config.json"), Read("taxonomy.json"), Read("preferences.md"));
    string? Read(string name) => File.Exists(paths.Get(name)) ? File.ReadAllText(paths.Get(name)) : null;
    public ManagementDocuments Save(string version, Func<ManagementDocuments, ManagementDocuments> edit)
    {
        using var held = ResourceLock.Try(paths, "settings") ?? throw new ResourceBusyException("Another settings save is in progress. Try again.");
        var before = Read();
        if (before.Version != version) throw new SettingsConflictException();
        var after = edit(before); after.Snapshot();
        var changes = new[] { ("config.json", before.Config, after.Config), ("taxonomy.json", before.Taxonomy, after.Taxonomy), ("preferences.md", before.Preferences, after.Preferences) }.Where(x => x.Item2 != x.Item3).ToArray();
        if (changes.Length > 1) throw new InvalidOperationException("Each save must change only one instance file.");
        if (changes.Length == 0) return after;
        var (name, original, updated) = changes[0];
        var temporary = paths.Get(name + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, updated ?? "");
            if (Read().Version != version) throw new SettingsConflictException();
            var backup = paths.Get("settings-backups"); Directory.CreateDirectory(backup);
            if (original is not null) File.WriteAllText(Path.Combine(backup, DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff") + "-" + name), original);
            File.Move(temporary, paths.Get(name), true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return after;
    }
    public static JsonObject Object(JsonObject parent, string key)
    {
        var actual = parent.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        if (parent[actual] is JsonObject child) return child;
        var created = new JsonObject(); parent[actual] = created; return created;
    }
    public static void Set(JsonObject parent, string key, JsonNode? value)
    {
        var actual = parent.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
        parent[actual] = value;
    }
    public static void SetMode(JsonObject parent, string mode, JsonNode? value)
    {
        foreach (var key in parent.Select(p => p.Key).Where(k => Platforms.Mode(k) == mode).ToArray()) parent.Remove(key);
        if (value is not null) parent[mode] = value;
    }
    public static string Json(JsonObject root) => root.ToJsonString(new() { WriteIndented = true }) + "\n";
    public static string[] Lines(string text) => text.Replace("\r", "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    // Change recognized bullet lines only. Keep prose, comments and other sections intact.
    static bool SectionMatches(string heading, string section) => section == "Never show — my rules"
        ? heading.StartsWith("never show", StringComparison.OrdinalIgnoreCase) && heading.Contains("my rules", StringComparison.OrdinalIgnoreCase)
        : section == "Plain-English policy" ? heading.StartsWith("plain-english policy", StringComparison.OrdinalIgnoreCase) || heading.StartsWith("plain english policy", StringComparison.OrdinalIgnoreCase)
        : heading.StartsWith(section, StringComparison.OrdinalIgnoreCase);
    public static string PreferenceSection(string? text, string section, string prefix, IEnumerable<string> entries)
    {
        var original = (text ?? "").Replace("\r\n", "\n");
        var lines = original.Split('\n').ToList();
        var visible = Regex.Replace(original, "<!--.*?(?:-->|$)", m => new string('\n', m.Value.Count(c => c == '\n')), RegexOptions.Singleline).Split('\n');
        var replacement = entries.Distinct().Select(x => "- " + prefix + x).ToArray();
        var remove = new List<int>(); int heading = -1; bool active = false;
        for (int n = 0; n < visible.Length; n++)
        {
            var line = visible[n];
            if (line.StartsWith("## ")) { active = SectionMatches(line[3..].Trim(), section); if (active && heading < 0) heading = n; }
            else if (active && line.StartsWith('-') && (prefix.Length == 0 || line[1..].TrimStart().StartsWith(prefix, StringComparison.Ordinal))) remove.Add(n);
        }
        if (heading < 0) { lines.AddRange(["", "## " + section, .. replacement]); return string.Join('\n', lines).TrimEnd() + "\n"; }
        foreach (var index in remove.AsEnumerable().Reverse()) lines.RemoveAt(index);
        lines.InsertRange(heading + 1, replacement); return string.Join('\n', lines).TrimEnd() + "\n";
    }
}
public sealed class SettingsConflictException() : Exception("Settings changed since this page was opened. Reload the page and apply your changes again; nothing was overwritten.");
