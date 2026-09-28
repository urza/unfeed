using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public sealed record IngestResult(int Found, int New, int Revised, int Media, int Failed);
public sealed class Ingest(InstancePaths paths, DbFactory factory, MediaFiles media)
{
    public static bool FriendsDirectory(string dir) => Path.GetFileName(dir).StartsWith("friends-", StringComparison.Ordinal) && !Path.GetFileName(dir).StartsWith("friends-timelines-", StringComparison.Ordinal);
    public async Task Register(string platform, string file, long? runId, CancellationToken ct)
    {
        var relative = Path.GetRelativePath(paths.Root, file); var info = new FileInfo(file); await using var db = factory.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO RawSnapshots (Platform,RunId,Path,Kind,CapturedAt,Parsed,Deleted,Bytes) VALUES ({platform},{runId},{relative},{(FriendsDirectory(info.DirectoryName!) ? "friends" : "feed")},{info.LastWriteTimeUtc},0,0,{info.Length})", ct);
    }
    public async Task Discover(string platform, CancellationToken ct)
    {
        var root = paths.Get("raw", platform); if (!Directory.Exists(root)) return;
        await using var db = factory.Open(); var live = await db.Runs.Where(r => r.Platform == platform && r.Status == "running" && r.Phase == "browser").Select(r => r.RawDir).ToListAsync(ct);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(x => Path.GetExtension(x) is ".json" or ".jsonl").Order()) if (!live.Contains(Path.GetFileName(Path.GetDirectoryName(file)))) await Register(platform, file, null, ct);
    }
    public async Task<IngestResult> File(string platform, string file, InstanceSnapshot instance, bool offline, CancellationToken ct)
    {
        await Register(platform, file, null, ct); var relative = Path.GetRelativePath(paths.Root, file); int found = 0, added = 0, revised = 0, files = 0;
        await using (var db = factory.Open()) await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.AttemptedAt, Clock.Now), ct);
        try
        {
            var people = FriendsDirectory(Path.GetDirectoryName(file)!); var parsed = PayloadParser.Parse(platform, await System.IO.File.ReadAllTextAsync(file, ct), people);
            await using (var db = factory.Open()) await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.Warning, parsed.Warnings.Count == 0 ? null : string.Join("; ", parsed.Warnings)), ct);
            if (parsed.Diagnostics.Count > 0) throw new FormatException(string.Join("; ", parsed.Diagnostics));
            if (people) foreach (var person in parsed.People)
            {
                await using var db = factory.Open(); Author? author;
                await using (var tx = await db.Database.BeginTransactionAsync(ct)) { author = await IdentityStore.Resolve(db, platform, person, true, ct, instance); await tx.CommitAsync(ct); } found++;
                if (author is not null && author.AvatarPath is null && person.Avatar is not null)
                {
                    var picture = await media.Image($"media/avatars/{platform}/{Identity.Safe(person.Id ?? author.Id.ToString())}{MediaFiles.Extension(person.Avatar)}", person.Avatar, offline, true, ct);
                    if (picture.Path is not null) { author.AvatarPath = picture.Path; await db.SaveChangesAsync(ct); }
                }
            }
            foreach (var observation in parsed.Posts)
            {
                found++; var result = await Store(observation, relative, instance, offline, ct); added += result.New; revised += result.Revised; files += result.Media;
            }
            await using (var db = factory.Open()) await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.Parsed, true).SetProperty(r => r.ParsedAt, Clock.Now).SetProperty(r => r.Error, (string?)null).SetProperty(r => r.BlockedParserVersion, (int?)null), ct);
            return new(found, added, revised, files, 0);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            int? blocked = e is FormatException or JsonException ? PayloadParser.Version : null;
            await using var db = factory.Open(); await db.RawSnapshots.Where(r => r.Path == relative).ExecuteUpdateAsync(s => s.SetProperty(r => r.Error, e.Message).SetProperty(r => r.Parsed, false).SetProperty(r => r.BlockedParserVersion, blocked), ct); return new(found, added, revised, files, 1);
        }
    }
    public static string ContentHash(Post p, IEnumerable<MediaSource> sources, IEnumerable<Media> rows)
    {
        // Canonical content serializer v1: explicit stable field order. Fetch URLs and retention paths never enter it.
        var obj = new { v = 1, p.AuthorId, p.ObservedAuthorName, p.ObservedAuthorUrl, p.PostedAt, p.Text, p.SharedAuthor, p.SharedText, p.SharedUrl, p.MemoryLabel, p.MemoryText, p.TagsJson, p.IsSponsored, p.IsSuggested, p.IsReel, p.IsEvent, media = sources.Select(s => new { s.Kind, s.SourceKey, bytes = s.Kind == "image" ? rows.FirstOrDefault(r => r.SourceKey == s.SourceKey)?.ContentHash : null }) };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj))));
    }
    async Task<IngestResult> Store(Observation o, string raw, InstanceSnapshot instance, bool offline, CancellationToken ct)
    {
        long id; bool isNew, changed; string originalHash; int files = 0;
        await using (var db = factory.Open())
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var author = await IdentityStore.Resolve(db, o.Post.Platform, new(o.AuthorKey, o.AuthorName, o.AuthorUrl), false, ct, instance);
            var p = await db.Posts.SingleOrDefaultAsync(p => p.Platform == o.Post.Platform && p.PlatformPostId == o.Post.PlatformPostId, ct); isNew = p is null;
            p ??= new() { Platform = o.Post.Platform, PlatformPostId = o.Post.PlatformPostId, RawRef = raw }; if (isNew) db.Add(p);
            originalHash = p.ContentHash; p.AuthorId = author?.Id;
            p.ObservedAuthorName = o.AuthorName; p.ObservedAuthorUrl = o.AuthorUrl; p.PostedAt = o.Post.PostedAt; p.Text = o.Post.Text; p.Permalink = o.Post.Permalink; p.LikeRef = o.Post.LikeRef; p.IsSponsored = o.Post.IsSponsored; p.IsSuggested = o.Post.IsSuggested; p.IsReel = o.Post.IsReel; p.IsEvent = o.Post.IsEvent; p.SharedAuthor = o.Post.SharedAuthor; p.SharedText = o.Post.SharedText; p.SharedUrl = o.Post.SharedUrl; p.MemoryLabel = o.Post.MemoryLabel; p.MemoryText = o.Post.MemoryText; p.TagsJson = o.Post.TagsJson; p.LatestRawRef = raw; p.MediaManifestJson = JsonSerializer.Serialize(o.Media);
            var rows = isNew ? [] : await db.Media.Where(m => m.PostId == p.Id).ToListAsync(ct); var hash = ContentHash(p, o.Media, rows); changed = hash != originalHash;
            if (changed) { if (!isNew) p.ContentRevision++; p.IngestReadyAt = null; p.LlmAttemptedAt = p.SummaryAttemptedAt = null; p.LlmError = p.SummaryError = null; p.LlmFailures = p.SummaryFailures = 0; p.ContentHash = hash; var authors = await db.Authors.ToListAsync(ct); Filters.Apply(p, new(author, authors, instance)); }
            foreach (var row in rows) row.IsCurrent = o.Media.Any(s => s.SourceKey == row.SourceKey);
            await db.SaveChangesAsync(ct); id = p.Id; await tx.CommitAsync(ct);
        }
        await using (var db = factory.Open())
        {
            var p = await db.Posts.SingleAsync(p => p.Id == id, ct); var rows = await db.Media.Where(m => m.PostId == id).ToListAsync(ct);
            int pos = 0;
            foreach (var source in o.Media)
            {
                pos++; var existing = rows.FirstOrDefault(m => m.SourceKey == source.SourceKey);
                if (existing is not null) { existing.IsCurrent = true; existing.OriginalUrl = source.Url; existing.Position = pos; if (existing.PrunedAt is not null || existing.Path is not null && System.IO.File.Exists(paths.Get(existing.Path)) || existing.Kind == "video") continue; }
                string suffix = rows.Any(m => m.Position == pos && m != existing) ? "-" + MediaFiles.Hash(Encoding.UTF8.GetBytes(source.SourceKey))[..12] : "";
                string rel = $"media/{p.Platform}/{Identity.Safe(p.PlatformPostId)}/{pos:00}{suffix}{(source.Kind == "video" ? ".mp4" : MediaFiles.Extension(source.Url))}";
                var row = existing ?? new Media { PostId = id, Position = pos, Kind = source.Kind, OriginalUrl = source.Url, SourceKey = source.SourceKey };
                if (source.Kind == "image") {
                    // Register identity before publishing bytes. A crash can recover the slot without assigning old bytes to a replacement source.
                    if (existing is null) { db.Add(row); rows.Add(row); await db.SaveChangesAsync(ct); }
                    var result = await media.Image(rel, source.Url, offline, false, ct);
                    if (result.Path is null) { Console.Error.WriteLine($"image {p.Platform}/#{p.Id} slot {pos}: {result.Error}"); db.Remove(row); rows.Remove(row); await db.SaveChangesAsync(ct); continue; }
                    row.Path = result.Path; row.ContentHash = result.Hash; row.Width = result.Info?.Width; row.Height = result.Info?.Height; files++; }
                else { if (System.IO.File.Exists(paths.Get(rel))) { row.Path = rel; row.ContentHash = MediaFiles.Hash(await System.IO.File.ReadAllBytesAsync(paths.Get(rel), ct)); } else if (p.Hidden || offline) continue; }
                if (existing is null && source.Kind != "image") { db.Add(row); rows.Add(row); }
            }
            var finalHash = ContentHash(p, o.Media, rows);
            if (!changed && finalHash != p.ContentHash) { p.ContentRevision++; p.LlmAttemptedAt = p.SummaryAttemptedAt = null; p.LlmError = p.SummaryError = null; p.LlmFailures = p.SummaryFailures = 0; }
            p.ContentHash = finalHash; p.IngestReadyAt = Clock.Now; await db.SaveChangesAsync(ct);
            return new(1, isNew ? 1 : 0, !isNew && (changed || finalHash != originalHash) ? 1 : 0, files, 0);
        }
    }
}
