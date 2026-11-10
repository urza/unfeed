using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.EntityFrameworkCore;
namespace Feed.Core.Application;

public static class VisibilityStore
{
    public sealed record Counts(int Total, int Hidden, int Shown);
    // Reapplies the current rules to stored posts without the model. Manage and the CLI share it, so a refilter gives
    // the same result from either place. The caller owns the transaction and the locks.
    public static async Task<Counts> Reapply(FeedDb db, IQueryable<Post> posts, InstanceSnapshot snapshot, CancellationToken ct)
    {
        var authors = await db.Authors.AsNoTracking().ToArrayAsync(ct); var byId = authors.ToDictionary(a => a.Id); var resolved = new ResolvedRules(authors, snapshot);
        int total = 0, hidden = 0, shown = 0; long last = 0;
        while (true)
        {
            var batch = await posts.Where(p => p.Id > last).OrderBy(p => p.Id).Take(500).ToArrayAsync(ct); if (batch.Length == 0) break;
            foreach (var p in batch)
            {
                bool was = p.Hidden; var rules = new RuleContext(p.AuthorId is { } id ? byId.GetValueOrDefault(id) : null, authors, snapshot, resolved);
                // Deliberate, also for a post whose result does not change: a verdict in flight carries the rules from
                // its selection (Processing: w.Rules), and only this revision makes its commit count as superseded.
                p.VisibilityRevision++;
                Filters.Apply(p, rules);
                if (p.CategoriesJson is not null) p.CategoriesJson = JsonSerializer.Serialize(Filters.Restrict(JsonSerializer.Deserialize<string[]>(p.CategoriesJson) ?? [], rules, p.Platform));
                total++; if (!was && p.Hidden) hidden++; if (was && !p.Hidden) shown++;
            }
            last = batch[^1].Id; await db.SaveChangesAsync(ct); db.ChangeTracker.Clear();
        }
        return new(total, hidden, shown);
    }
}
