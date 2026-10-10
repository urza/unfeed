using System.Text.Json;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public static class CaptureIndex
{
    // Backfill diagnostic evidence without replaying posts or resolving historical failures.
    public static async Task<int> Run(DbFactory factory, string platform, int limit, CancellationToken ct)
    {
        using var held = ResourceLock.Try(factory.Paths,"ingest-"+platform) ?? throw new ResourceBusyException("ingest busy");
        await using var db = factory.Open();
        // Backfill only verified, single-visit provenance; legacy and duplicate origins remain unassigned.
        var links = await db.RawSnapshots.Where(r => r.Platform == platform && !r.Deleted && r.CaptureToken == null && r.ContextJson != null)
            .OrderByDescending(r => r.Id).Take(limit).ToArrayAsync(ct);
        foreach (var raw in links)
        {
            try {
                var file = factory.Paths.Get(raw.Path);
                if (!System.IO.File.Exists(file + ".visits")) raw.CaptureToken = CaptureContext.Read(file, await System.IO.File.ReadAllTextAsync(file, ct))?.Visit;
            } catch (Exception e) when (e is IOException or FormatException) { /* Keep unknown provenance unassigned. */ }
        }
        await db.SaveChangesAsync(ct);
        var rows = await db.RawSnapshots.Where(r => r.Platform == platform && !r.Deleted && r.ParserVersion != PayloadParser.Version && (r.Error != null || r.Warning != null)
            && !db.Runs.Any(run => run.Id == r.RunId && run.Status == "running" && run.Phase == "browser")).OrderByDescending(r=>r.Id).Take(limit).ToArrayAsync(ct);
        int count=0;
        foreach(var raw in rows)
        {
            try
            {
                var file=factory.Paths.Get(raw.Path);var body=await File.ReadAllTextAsync(file,ct);var context=CaptureContext.Read(file,body);
                var parsed=CaptureRecovery.ParseSaved(platform,body,file,context:context);
                // Changed outcomes need a real replay. Indexing must not clear or mask them.
                if (raw.Parsed && parsed.Diagnostics.Count > 0 || !raw.Parsed && parsed.Diagnostics.Count == 0) continue;
                await using var tx=await db.Database.BeginTransactionAsync(ct);
                await db.RawDiagnostics.Where(d=>d.RawSnapshotId==raw.Id).ExecuteDeleteAsync(ct);
                foreach(var d in parsed.Issues) db.RawDiagnostics.Add(new(){RawSnapshotId=raw.Id,Code=d.Code,Fingerprint=d.Fingerprint,Path=d.Path,Impact=d.Impact,Evidence=d.Evidence,Root=d.Root,ParserVersion=PayloadParser.Version});
                raw.CaptureToken=System.IO.File.Exists(file+".visits")?null:context?.Visit;raw.ContextJson=context is null?null:JsonSerializer.Serialize(context);raw.ClassificationJson=JsonSerializer.Serialize(parsed.Parts);raw.ParserVersion=PayloadParser.Version;
                await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);count++;
            }
            catch(Exception e) when(e is IOException or FormatException or JsonException){Console.Error.WriteLine($"capture-index #{raw.Id}: {e.Message}");}
        }
        return count;
    }
}
