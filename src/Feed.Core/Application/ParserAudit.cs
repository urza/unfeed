using System.Text.Json;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public static class ParserAudit
{
    public static async Task<object[]> Read(DbFactory factory, string platform, int limit, long[]? ids, long? runId, bool failed, CancellationToken ct)
    {
        if (limit is < 1 or > 1000 || (ids?.Length ?? 0) > limit) throw new ArgumentException("Audit limit must be 1..1000 and cover the explicit IDs");
        await using var db = factory.Open();
        var query = db.RawSnapshots.AsNoTracking().Where(r => r.Platform == platform && !r.Deleted);
        if (ids is not null) query = query.Where(r => ids.Contains(r.Id));
        if (runId is not null) query = query.Where(r => r.RunId == runId);
        if (failed) query = query.Where(r => !r.Parsed && r.Error != null);
        var rows = await query.OrderByDescending(r => r.Id).Take(limit).Select(r => new { r.Id, r.Path, r.Platform, r.RunId, r.Parsed, r.Error }).ToArrayAsync(ct);
        if (ids is not null && rows.Length != ids.Distinct().Count()) throw new ArgumentException("Some requested captures are unavailable or outside the selected platform/scope");
        var reports = new List<object>();
        foreach (var raw in rows)
        {
            try
            {
                var file = factory.Paths.Get(raw.Path); var body = await File.ReadAllTextAsync(file, ct); var context = CaptureContext.Read(file, body);
                var native = PayloadParser.Parse(platform, body, context: context); var effective = CaptureRecovery.ParseSaved(platform, body, file, context: context);
                var postIds = effective.Posts.Select(o => o.Post.PlatformPostId).ToArray();
                var stored = await db.Posts.AsNoTracking().Where(p => p.Platform == platform && postIds.Contains(p.PlatformPostId)).Select(p => new { p.PlatformPostId, p.Text, p.Permalink, p.PostedAt, p.MediaManifestJson, p.ContentRevision, p.ContentHash, p.RawRef, p.LatestRawRef }).ToArrayAsync(ct);
                reports.Add(new { raw.Id, raw.RunId, raw.Parsed, PreviousError = raw.Error, BodyHash = CaptureContext.Hash(body), Context = context, ParserVersion = PayloadParser.Version,
                    Native = new { native.Parts, native.Issues, native.Diagnostics, native.ExplicitEmpty }, Effective = effective, Stored = stored });
            }
            catch (Exception e) when (e is IOException or FormatException or JsonException) { reports.Add(new { raw.Id, Error = e.Message }); }
        }
        return reports.ToArray();
    }
}
