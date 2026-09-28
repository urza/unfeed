using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

// Browser observations and sweep advancement share one durable boundary.
public sealed class TimelineCoverage(DbFactory factory)
{
    public const int MaxAttempts = 3;
    public async Task<List<SweepTarget>> Select(string platform, int budget, CancellationToken ct = default, bool retriesOnly = false)
    {
        await using var db = factory.Open();
        var cycle = await db.Get($"sweep:{platform}:cycle", ct);
        if (cycle is null || !await db.SweepTargets.AnyAsync(t => t.Platform == platform && t.CycleId == cycle && (t.State == "pending" || t.State == "visiting" || t.State == "retry"), ct))
        {
            if (retriesOnly) return [];
            cycle = Guid.NewGuid().ToString("N");
            var authors = await db.Authors.Where(a => a.Platform == platform && a.IsFriend).OrderBy(a => a.DisplayName).ThenBy(a => a.Id).ToListAsync(ct);
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            for (var position = 0; position < authors.Count; position++) db.SweepTargets.Add(new() { Platform = platform, CycleId = cycle, AuthorId = authors[position].Id, Position = position });
            await db.SaveChangesAsync(ct); await db.Put($"sweep:{platform}:cycle", cycle, ct); await tx.CommitAsync(ct);
        }
        if (retriesOnly && await db.SweepTargets.AnyAsync(t => t.Platform == platform && t.CycleId == cycle && (t.State == "pending" || t.State == "visiting"), ct)) return [];
        return await db.SweepTargets.Where(t => t.Platform == platform && t.CycleId == cycle && (!retriesOnly && t.State == "pending" || t.State == "retry" && t.Attempts < MaxAttempts && t.RetryAt <= Clock.Now))
            .OrderBy(t => t.State == "pending" ? 0 : 1).ThenBy(t => t.Position).ThenBy(t => t.AuthorId).Take(budget).ToListAsync(ct);
    }
    public async Task Complete(TimelineVisit visit, SweepTarget? target, CancellationToken ct = default)
    {
        await using var db = factory.Open(); await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.TimelineVisits.Add(visit); await db.SaveChangesAsync(ct);
        if (target is not null)
        {
            var row = await db.SweepTargets.SingleAsync(t => t.Platform == target.Platform && t.CycleId == target.CycleId && t.AuthorId == target.AuthorId, ct);
            row.Attempts++; row.AttemptedAt = visit.At; row.VisitId = visit.Id;
            // Checkpoints and cancellation stop this visit; never schedule an automatic
            // browser retry of an authentication challenge or interrupted operation.
            bool retryable = visit.CaptureStatus == "incomplete" && visit.Status is "rendered" or "unrendered" or "error";
            row.State = retryable && row.Attempts < MaxAttempts ? "retry" : "done";
            row.RetryAt = row.State == "retry" ? Clock.Now.AddHours(row.Attempts) : null;
            await db.SaveChangesAsync(ct);
        }
        await tx.CommitAsync(ct);
    }
}
