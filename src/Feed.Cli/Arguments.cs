namespace Feed.Cli;
public sealed class Arguments
{
    public string Command { get; } public List<string> Positionals { get; } = []; public Dictionary<string, string> Options { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Arguments(string[] args)
    {
        Command = args.Length == 0 ? "help" : args[0].ToLowerInvariant();
        for (int i = 1; i < args.Length; i++) { var arg = args[i]; if (!arg.StartsWith("--")) { Positionals.Add(arg); continue; } var pair = arg[2..].Split('=', 2); Options[pair[0]] = pair.Length == 2 ? pair[1] : i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[++i] : "true"; }
    }
    public string? Get(string key) => Options.GetValueOrDefault(key);
    public bool Flag(string key) => Get(key)?.ToLowerInvariant() is "true" or "1" or "yes";
    public int? Number(string key) { if (Get(key) is not { } value) return null; if (!int.TryParse(value, out var number) || number < 1) throw new ArgumentException($"--{key} must be at least 1"); return number; }
    public DateTime? Date(string key) { if (Get(key) is not { } value) return null; if (!DateTime.TryParse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) throw new ArgumentException($"--{key} must be a UTC date"); return date; }
    public string Platform(bool all = false, string? fallback = null) { var p = Get("platform") ?? fallback ?? throw new ArgumentException("--platform is required"); if (!Feed.Core.Domain.Platforms.All.Contains(p) && !(all && p == "all")) throw new ArgumentException("unknown platform: " + p); return p; }
}
