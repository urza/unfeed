using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Feed.Core.Domain;

public static class Platforms
{
    public static readonly string[] All = ["facebook", "instagram"];
    public static readonly string[] Modes = ["home", "close_friends", "all_followed"];
    public static string Mode(string s) => s.Replace('-', '_').ToLowerInvariant();
    public static string Prefix(string p) => p == "facebook" ? "fb" : "ig";
    public static string Home(string p) => $"https://www.{p}.com/";
}
public sealed record FeedConfig
{
    public string? Timezone { get; init; }
    public ImmutableDictionary<string, PlatformConfig> Platforms { get; init; } = ImmutableDictionary<string, PlatformConfig>.Empty;
    public LlmConfig Llm { get; init; } = new();
    public FilterConfig Filters { get; init; } = new();
    public SchedulerConfig Scheduler { get; init; } = new();
    public int RawRetentionDays { get; init; } = 60;
    public int HiddenMediaRetentionDays { get; init; } = 7;
    public int HiddenVideoRetentionDays { get; init; } = 1;
    public int VideoRetentionDays { get; init; } = 180;
    public int MaxVideoMb { get; init; } = 50;
    public VideoContextConfig VideoContext { get; init; } = new();
    public UiConfig Ui { get; init; } = new();
    public BackgroundConfig Backgrounds { get; init; } = new();
    public WebConfig Web { get; init; } = new();
    public BrowserConfig Browser { get; init; } = new();
    public LikebackConfig Likeback { get; init; } = new();
    public bool Enabled(string p) => Platforms.TryGetValue(p, out var c) && c.Enabled;
    public PlatformConfig Platform(string p) => Platforms.GetValueOrDefault(p) ?? new() { Enabled = false };
    public TimeZoneInfo Zone => ResolveZone(Timezone);
    public static TimeZoneInfo ResolveZone(string? id) { try { return id is null ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id); } catch (TimeZoneNotFoundException) { return TimeZoneInfo.Local; } catch (InvalidTimeZoneException) { return TimeZoneInfo.Local; } }
}
public sealed record PlatformConfig
{
    public bool Enabled { get; init; } = true;
    public ImmutableArray<string> CloseFriends { get; init; } = [];
    public ImmutableArray<string> HomeTimelineAuthors { get; init; } = [];
    public int SweepLimit { get; init; } = 25;
    public ImmutableDictionary<string, ImmutableArray<string>> Schedule { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableDictionary<string, ImmutableArray<string>> ScheduleDays { get; init; } = ImmutableDictionary<string, ImmutableArray<string>>.Empty;
    public ImmutableDictionary<string, ScheduleInterval> ScheduleIntervals { get; init; } = ImmutableDictionary<string, ScheduleInterval>.Empty;
    public bool Likeback { get; init; }
    public string? SelfUsername { get; init; }
    public bool ScheduledOn(string mode, DateOnly date)
    {
        mode = Platforms.Mode(mode);
        var days = ScheduleDays.GetValueOrDefault(mode);
        if (!days.IsDefaultOrEmpty && !days.Contains(date.ToString("ddd", System.Globalization.CultureInfo.InvariantCulture).ToLowerInvariant())) return false;
        if (!ScheduleIntervals.TryGetValue(mode, out var interval)) return true;
        var elapsed = date.DayNumber - interval.StartDate.DayNumber;
        return elapsed >= 0 && elapsed % interval.Days == 0;
    }
}
public sealed record ScheduleInterval { public int Days { get; init; } = 1; public DateOnly StartDate { get; init; } }
public sealed record LlmConfig
{
    public bool Enabled { get; init; }
    public string BaseUrl { get; init; } = "";
    public string ApiKey { get; init; } = "";
    public string Model { get; init; } = "";
    public string FallbackBaseUrl { get; init; } = "";
    public string FallbackApiKey { get; init; } = "";
    public bool Vision { get; init; } = true;
    public int VisionMaxImages { get; init; } = 3;
    public int MaxTokens { get; init; } = 4000;
    public ImmutableArray<int> TokenRetryBudgets { get; init; } = [8000, 16000];
    public int TokenRetryDelayMinutes { get; init; } = 30;
    public int SummaryMaxTokens { get; init; } = 4000;
    public bool? SummaryEnableThinking { get; init; }
    public ImmutableArray<string> SummaryLanguages { get; init; } = ["English", "Czech", "Slovak"];
    public int Threshold { get; init; } = 4;
    public int TimeoutSeconds { get; init; } = 180;
    public int Batch { get; init; } = 20;
    public int Parallel { get; init; } = 1;
}
public sealed record VideoContextConfig
{
    public bool Enabled { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public ImmutableArray<string> CaptionLanguages { get; init; } = ["en"];
}
public sealed record FilterConfig
{
    public ImmutableArray<string> BlockedTypes { get; init; } = ["sponsored", "suggested", "event"];
    public string Audience { get; init; } = "friends_and_followed";
    public string FriendTagException { get; init; } = "with";
    public ImmutableArray<string> AlwaysShowBypasses { get; init; } = ["keyword", "llm"];
}
public sealed record SchedulerConfig { public bool Enabled { get; init; } = true; public int JitterMinutes { get; init; } = 10; public int ManualCooldownMinutes { get; init; } = 30; public int RunRequestTtlMinutes { get; init; } = 60; }
public sealed record UiConfig { public int RenderCap { get; init; } = 300; public int CoverageStaleHours { get; init; } = 48; public string DefaultView { get; init; } = "all"; public string LogLevel { get; init; } = "info"; public StackConfig Stack { get; init; } = new(); }
public sealed record StackConfig { public int MinPosts { get; init; } = 3; public int WindowDays { get; init; } = 7; }
public sealed record BackgroundConfig { public string Source { get; init; } = "bing"; public double BlurPx { get; init; } = 4; }
public sealed record WebConfig { public string Host { get; init; } = "127.0.0.1"; public int Port { get; init; } = 8000; }
public sealed record BrowserConfig { public bool StageProfiles { get; init; } = true; public string? StageDir { get; init; } public string Display { get; init; } = ":99"; public bool NoSandbox { get; init; } }
public sealed record LikebackConfig { public double MinDelaySeconds { get; init; } = 2; public double MaxDelaySeconds { get; init; } = 10; }
public sealed record Taxonomy { public ImmutableArray<Category> Categories { get; init; } = []; public ImmutableArray<ViewDefinition> Views { get; init; } = []; }
public sealed record Category { public string Key { get; init; } = ""; public string Label { get; init; } = ""; public string Definition { get; init; } = ""; public bool Default { get; init; } public ImmutableArray<string> CloseFriendsOnly { get; init; } = []; }
public sealed record ViewDefinition { public string Key { get; init; } = ""; public string Label { get; init; } = ""; public string? Category { get; init; } public ImmutableArray<string>? Authors { get; init; } public RareConfig? Rare { get; init; } public ImmutableArray<string>? Union { get; init; } }
public sealed record RareConfig { public int MaxPosts { get; init; } = 3; public int WindowDays { get; init; } = 90; }
public sealed record Preferences(ImmutableArray<string> Keywords, ImmutableArray<string> Mutes, ImmutableArray<string> Shows, ImmutableArray<string> Policy, ImmutableArray<string> Learned, ImmutableArray<string> HashLines)
{
    public static Preferences Parse(string text)
    {
        var keywords = new List<string>(); var mutes = new List<string>(); var shows = new List<string>(); var policy = new List<string>(); var learned = new List<string>(); var hash = new List<string>();
        text = Regex.Replace(text, "<!--.*?(?:-->|$)", "", RegexOptions.Singleline);
        string section = "";
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            if (raw.StartsWith("## ")) { section = raw[3..].Trim().ToLowerInvariant(); continue; }
            if (!raw.StartsWith('-')) continue;
            var line = raw[1..].Trim();
            if (section.StartsWith("never show") && section.Contains("my rules")) { if (!line.StartsWith("keyword:")) throw new FormatException("Never show requires - keyword: <text>"); keywords.Add(line[8..].Trim()); }
            else if (section.StartsWith("muted people")) { if (!line.StartsWith("mute:")) throw new FormatException("Muted people requires - mute: <name or ref>"); mutes.Add(line[5..].Trim()); }
            else if (section.StartsWith("always show") && line.StartsWith("show:")) { shows.Add(line[5..].Trim()); hash.Add("show:" + raw.Trim()); }
            else if (section.StartsWith("plain-english policy") || section.StartsWith("plain english policy")) { policy.Add(line); hash.Add(raw.Trim()); }
            else if (section.StartsWith("learned from thumbs")) { learned.Add(line); hash.Add("learned:" + raw.Trim()); }
        }
        return new([.. keywords], [.. mutes], [.. shows], [.. policy], [.. learned], [.. hash]);
    }
}
public sealed record InstanceSnapshot(FeedConfig Config, Taxonomy Taxonomy, Preferences Preferences);
public static class InstanceValidation
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower, PropertyNameCaseInsensitive = true, ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static T Parse<T>(string? json) where T : new()
    {
        if (json is null) return new();
        // Null objects/lists are not optional keys; reject before typed deserialization.
        using var document = JsonDocument.Parse(json, new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        RejectNulls(document.RootElement);
        var result = JsonSerializer.Deserialize<T>(json, Json) ?? throw new FormatException("Expected an object");
        if (result is FeedConfig config)
        {
            try
            {
                var normalized = config with { Platforms = config.Platforms.ToImmutableDictionary(p => p.Key.ToLowerInvariant(), p => p.Value with { Schedule = p.Value.Schedule.ToImmutableDictionary(x => Platforms.Mode(x.Key), x => x.Value), ScheduleDays = p.Value.ScheduleDays.ToImmutableDictionary(x => Platforms.Mode(x.Key), x => x.Value), ScheduleIntervals = p.Value.ScheduleIntervals.ToImmutableDictionary(x => Platforms.Mode(x.Key), x => x.Value) }) };
                return (T)(object)normalized;
            }
            catch (ArgumentException e) { throw new FormatException("Duplicate case-insensitive platform or mode key", e); }
        }
        return result;
    }
    static void RejectNulls(JsonElement e) { if (e.ValueKind == JsonValueKind.Null) throw new FormatException("Use an absent key instead of null"); if (e.ValueKind == JsonValueKind.Object) foreach (var p in e.EnumerateObject()) RejectNulls(p.Value); if (e.ValueKind == JsonValueKind.Array) foreach (var p in e.EnumerateArray()) RejectNulls(p); }
    public static void Validate(FeedConfig c, Taxonomy t)
    {
        static void Require(bool condition, string message) { if (!condition) throw new FormatException(message); }
        static void Choice(string s, params string[] values) { if (!values.Contains(s)) throw new FormatException($"Unknown value: {s}"); }
        Require(c.Llm.Batch >= 1 && c.Llm.Parallel >= 1 && c.Ui.RenderCap >= 1 && c.Ui.CoverageStaleHours >= 1, "batch, parallel, render_cap and coverage_stale_hours must be at least 1");
        Require(c.Llm.Threshold is >= 0 and <= 10 && c.Llm.TimeoutSeconds >= 10 && c.Llm.MaxTokens > 0 && c.Llm.SummaryMaxTokens > 0 && c.Llm.VisionMaxImages >= 0, "Invalid model bounds");
        Require(new[] { c.RawRetentionDays, c.HiddenMediaRetentionDays, c.HiddenVideoRetentionDays, c.VideoRetentionDays, c.MaxVideoMb, c.Scheduler.JitterMinutes, c.Scheduler.ManualCooldownMinutes, c.Scheduler.RunRequestTtlMinutes, c.Ui.Stack.MinPosts, c.Ui.Stack.WindowDays }.All(n => n >= 0), "Retention, size, cooldown and stack values cannot be negative");
        Require(c.Llm.TokenRetryDelayMinutes >= 1 && c.Llm.TokenRetryBudgets.All(n => n > 0) && c.Llm.TokenRetryBudgets.SequenceEqual(c.Llm.TokenRetryBudgets.Distinct().Order()), "Token retry budgets must be positive and strictly increasing; delay must be at least 1 minute");
        Require(c.Web.Port is >= 0 and <= 65535, "Invalid web port");
        Require(c.VideoContext.TimeoutSeconds is >= 5 and <= 120 && c.VideoContext.CaptionLanguages.Length is >= 1 and <= 10
            && c.VideoContext.CaptionLanguages.All(l => Regex.IsMatch(l, "^[a-zA-Z]{2,3}(-[a-zA-Z0-9]{2,8})*$")), "Invalid video_context timeout or caption_languages");
        Require(double.IsFinite(c.Backgrounds.BlurPx) && c.Backgrounds.BlurPx >= 0 && double.IsFinite(c.Likeback.MinDelaySeconds) && double.IsFinite(c.Likeback.MaxDelaySeconds) && c.Likeback.MinDelaySeconds >= 0 && c.Likeback.MaxDelaySeconds >= c.Likeback.MinDelaySeconds, "Invalid blur or delay bounds");
        Choice(c.Ui.LogLevel, "trace", "debug", "info", "warn", "error");
        Choice(c.Filters.Audience, "friends_and_followed", "all_captured"); Choice(c.Filters.FriendTagException, "none", "with", "any");
        foreach (var type in c.Filters.BlockedTypes) Choice(type, "sponsored", "suggested", "event", "reel");
        foreach (var gate in c.Filters.AlwaysShowBypasses) Choice(gate, "types", "audience", "keyword", "llm");
        foreach (var (key, p) in c.Platforms)
        {
            Choice(key, Platforms.All); Require(p.SweepLimit >= 1, "sweep_limit must be at least 1");
            foreach (var (mode, times) in p.Schedule) { Choice(Platforms.Mode(mode), Platforms.Modes); foreach (var time in times) Require(Regex.IsMatch(time, "^([01][0-9]|2[0-3]):[0-5][0-9]$"), $"Invalid schedule slot: {time}"); }
            foreach (var (mode, days) in p.ScheduleDays) { Choice(Platforms.Mode(mode), Platforms.Modes); foreach (var day in days) Choice(day, "mon", "tue", "wed", "thu", "fri", "sat", "sun"); }
            foreach (var (mode, interval) in p.ScheduleIntervals) { Choice(Platforms.Mode(mode), Platforms.Modes); Require(interval.Days >= 1 && interval.StartDate != default, "schedule_intervals requires days >= 1 and start_date (yyyy-MM-dd)"); }
        }
        var cats = t.Categories.Select(x => x.Key).ToArray(); var views = t.Views.Select(x => x.Key).ToArray();
        Require(cats.Distinct().Count() == cats.Length && views.Distinct().Count() == views.Length, "Duplicate taxonomy key");
        Require(cats.Concat(views).All(x => Regex.IsMatch(x, "^[a-z0-9_-]+$")), "Taxonomy keys must be slugs");
        Require(t.Categories.Count(x => x.Default) <= 1 && !views.Contains("all"), "Invalid default or reserved all view");
        foreach (var cat in t.Categories) foreach (var p in cat.CloseFriendsOnly) Choice(p, Platforms.All);
        foreach (var v in t.Views)
        {
            Require((v.Category is not null || v.Authors is not null ? 1 : 0) + (v.Rare is null ? 0 : 1) + (v.Union is null ? 0 : 1) == 1, $"View {v.Key} needs category, authors, category with authors, rare, or union; other combinations are invalid");
            if (v.Category is not null) Require(cats.Contains(v.Category), $"Unknown category {v.Category}");
            if (v.Rare is not null) Require(v.Rare.MaxPosts >= 1 && v.Rare.WindowDays >= 1, "Rare values must be at least 1");
            if (v.Union is { } members) foreach (var member in members) Require(t.Views.Any(x => x.Key == member && x.Union is null), $"Invalid union member: {member}");
        }
    }
}
