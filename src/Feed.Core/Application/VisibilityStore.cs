using System.Text.Json;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Feed.Core.Application;

public static class VisibilityStore
{
    public sealed record Counts(int Total, int Hidden, int Shown);
    // Reapplies the current rules to stored posts without the model. Manage and the CLI share it, so a refilter gives
    // the same result from either place. The caller owns the transaction and the locks.
    public static async Task<Counts> Reapply(FeedDb db, IQueryable<Post> posts, InstanceSnapshot snapshot, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct);
        var conn = (SqliteConnection)db.Database.GetDbConnection();
        var outerTx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
        var authors = await SqliteDates.ReadAuthorsAsync(conn, outerTx, ct);
        var byId = new Dictionary<long, Author>(authors.Length);
        foreach (var a in authors) byId[a.Id] = a;
        var resolved = new ResolvedRules(authors, snapshot);

        if (ReferenceEquals(posts, db.Posts))
            return await ReapplyAllFast(db, conn, authors, byId, resolved, snapshot, ct);

        int total = 0, hidden = 0, shown = 0; long last = 0;
        var bumpIds = new List<long>(500);
        var changed = new List<Post>(64);
        while (true)
        {
            // Project only the columns visibility and category restriction read, bypassing EF change-tracking snapshots
            // across all 57 columns of wide Posts rows.
            var batch = await posts.AsNoTracking().Where(p => p.Id > last).OrderBy(p => p.Id).Take(500).Select(p => new Post
            {
                Id = p.Id, Platform = p.Platform, AuthorId = p.AuthorId, ObservedAuthorName = p.ObservedAuthorName,
                Text = p.Text, SharedText = p.SharedText, TagsJson = p.TagsJson,
                IsSponsored = p.IsSponsored, IsSuggested = p.IsSuggested, IsReel = p.IsReel, IsEvent = p.IsEvent,
                ContentRevision = p.ContentRevision, CategoriesJson = p.CategoriesJson, LlmScore = p.LlmScore,
                LlmReason = p.LlmReason, VerdictContentRevision = p.VerdictContentRevision,
                Hidden = p.Hidden, HiddenBy = p.HiddenBy, HiddenReason = p.HiddenReason, HiddenAt = p.HiddenAt,
                VisibilityRevision = p.VisibilityRevision, OwnerUnhiddenRevision = p.OwnerUnhiddenRevision
            }).ToArrayAsync(ct);
            if (batch.Length == 0) break;
            bumpIds.Clear(); changed.Clear();
            foreach (var p in batch)
            {
                bool was = p.Hidden; string? oldBy = p.HiddenBy, oldReason = p.HiddenReason, oldCategories = p.CategoriesJson; DateTime? oldAt = p.HiddenAt;
                var rules = new RuleContext(p.AuthorId is { } id ? byId.GetValueOrDefault(id) : null, authors, snapshot, resolved);
                // Deliberate, also for a post whose result does not change: a verdict in flight carries the rules from
                // its selection (Processing: w.Rules), and only this revision makes its commit count as superseded.
                p.VisibilityRevision++;
                Filters.Apply(p, rules);
                if (p.CategoriesJson is not null)
                    p.CategoriesJson = resolved.RestrictJson(p.CategoriesJson, rules.CloseFriend, p.Platform);
                total++; if (!was && p.Hidden) hidden++; if (was && !p.Hidden) shown++;
                if (p.Hidden == was && p.HiddenBy == oldBy && p.HiddenReason == oldReason && p.HiddenAt == oldAt && p.CategoriesJson == oldCategories)
                    bumpIds.Add(p.Id);
                else
                    changed.Add(p);
            }
            var ownsTx = db.Database.CurrentTransaction is null;
            var tx = ownsTx ? (SqliteTransaction)await conn.BeginTransactionAsync(ct) : (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            try
            {
                if (bumpIds.Count > 0)
                {
                    await using var bump = conn.CreateCommand();
                    bump.Transaction = tx;
                    bump.CommandText = "UPDATE Posts SET VisibilityRevision = VisibilityRevision + 1 WHERE Id IN (SELECT value FROM json_each($ids))";
                    bump.Parameters.AddWithValue("$ids", JsonSerializer.Serialize(bumpIds));
                    await bump.ExecuteNonQueryAsync(ct);
                }
                if (changed.Count > 0) await WriteChanged(conn, tx, changed, ct);
                if (ownsTx) await tx.CommitAsync(ct);
            }
            finally { if (ownsTx) await tx.DisposeAsync(); }
            last = batch[^1].Id; db.ChangeTracker.Clear();
        }
        return new(total, hidden, shown);
    }

    // Full-table refilter fast path: streams narrow columns via SqliteDataReader (avoiding EF materialization and
    // skipping Text/SharedText reads when no keywords exist) and bumps VisibilityRevision with a sequential PK range update.
    static async Task<Counts> ReapplyAllFast(FeedDb db, SqliteConnection conn, Author[] authors, Dictionary<long, Author> byId, ResolvedRules resolved, InstanceSnapshot snapshot, CancellationToken ct)
    {
        int total = 0, hidden = 0, shown = 0; long last = 0;
        bool readText = resolved.HasKeywords;
        var changed = new List<Post>(128);
        var scratch = new Post();
        const int BatchSize = 2000;
        var selectSql = readText
            ? "SELECT Id, Platform, AuthorId, ObservedAuthorName, Text, SharedText, TagsJson, IsSponsored, IsSuggested, IsReel, IsEvent, ContentRevision, CategoriesJson, LlmScore, LlmReason, VerdictContentRevision, Hidden, HiddenBy, HiddenReason, HiddenAt, VisibilityRevision, OwnerUnhiddenRevision FROM Posts WHERE Id > $last ORDER BY Id LIMIT $lim"
            : "SELECT Id, Platform, AuthorId, ObservedAuthorName, NULL, NULL, TagsJson, IsSponsored, IsSuggested, IsReel, IsEvent, ContentRevision, CategoriesJson, LlmScore, LlmReason, VerdictContentRevision, Hidden, HiddenBy, HiddenReason, HiddenAt, VisibilityRevision, OwnerUnhiddenRevision FROM Posts WHERE Id > $last ORDER BY Id LIMIT $lim";

        while (true)
        {
            changed.Clear();
            long batchLo = last, batchHi = last;
            int batchCount = 0;
            var outerTx = db.Database.CurrentTransaction?.GetDbTransaction() as SqliteTransaction;
            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = outerTx;
                cmd.CommandText = selectSql;
                cmd.Parameters.AddWithValue("$last", last);
                cmd.Parameters.AddWithValue("$lim", BatchSize);
                await using var r = await cmd.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    batchCount++;
                    long id = r.GetInt64(0);
                    batchHi = id;
                    scratch.Id = id;
                    scratch.Platform = r.GetString(1);
                    scratch.AuthorId = r.IsDBNull(2) ? null : r.GetInt64(2);
                    scratch.ObservedAuthorName = r.IsDBNull(3) ? null : r.GetString(3);
                    scratch.Text = readText && !r.IsDBNull(4) ? r.GetString(4) : null;
                    scratch.SharedText = readText && !r.IsDBNull(5) ? r.GetString(5) : null;
                    scratch.TagsJson = r.IsDBNull(6) ? null : r.GetString(6);
                    scratch.IsSponsored = r.GetInt32(7) != 0;
                    scratch.IsSuggested = r.GetInt32(8) != 0;
                    scratch.IsReel = r.GetInt32(9) != 0;
                    scratch.IsEvent = r.GetInt32(10) != 0;
                    scratch.ContentRevision = r.GetInt32(11);
                    scratch.CategoriesJson = r.IsDBNull(12) ? null : r.GetString(12);
                    scratch.LlmScore = r.IsDBNull(13) ? null : r.GetInt32(13);
                    scratch.LlmReason = r.IsDBNull(14) ? null : r.GetString(14);
                    scratch.VerdictContentRevision = r.IsDBNull(15) ? null : r.GetInt32(15);
                    scratch.Hidden = r.GetInt32(16) != 0;
                    scratch.HiddenBy = r.IsDBNull(17) ? null : r.GetString(17);
                    scratch.HiddenReason = r.IsDBNull(18) ? null : r.GetString(18);
                    scratch.HiddenAt = r.IsDBNull(19) ? null : SqliteDates.Parse(r.GetString(19));
                    scratch.VisibilityRevision = r.GetInt32(20);
                    scratch.OwnerUnhiddenRevision = r.IsDBNull(21) ? null : r.GetInt32(21);

                    bool was = scratch.Hidden;
                    string? oldBy = scratch.HiddenBy, oldReason = scratch.HiddenReason, oldCategories = scratch.CategoriesJson;
                    DateTime? oldAt = scratch.HiddenAt;
                    var rules = new RuleContext(scratch.AuthorId is { } aid ? byId.GetValueOrDefault(aid) : null, authors, snapshot, resolved);
                    scratch.VisibilityRevision++;
                    Filters.Apply(scratch, rules);
                    if (scratch.CategoriesJson is not null)
                        scratch.CategoriesJson = resolved.RestrictJson(scratch.CategoriesJson, rules.CloseFriend, scratch.Platform);
                    total++;
                    if (!was && scratch.Hidden) hidden++;
                    if (was && !scratch.Hidden) shown++;

                    if (scratch.Hidden != was || scratch.HiddenBy != oldBy || scratch.HiddenReason != oldReason || scratch.HiddenAt != oldAt || scratch.CategoriesJson != oldCategories)
                    {
                        changed.Add(new Post
                        {
                            Id = scratch.Id, Hidden = scratch.Hidden, HiddenBy = scratch.HiddenBy,
                            HiddenReason = scratch.HiddenReason, HiddenAt = scratch.HiddenAt,
                            VisibilityRevision = scratch.VisibilityRevision, CategoriesJson = scratch.CategoriesJson
                        });
                    }
                }
            }
            if (batchCount == 0) break;

            var ownsTx = db.Database.CurrentTransaction is null;
            var tx = ownsTx ? (SqliteTransaction)await conn.BeginTransactionAsync(ct) : (SqliteTransaction)db.Database.CurrentTransaction!.GetDbTransaction();
            try
            {
                await using (var bump = conn.CreateCommand())
                {
                    bump.Transaction = tx;
                    bump.CommandText = "UPDATE Posts SET VisibilityRevision = VisibilityRevision + 1 WHERE Id > $lo AND Id <= $hi";
                    bump.Parameters.AddWithValue("$lo", batchLo);
                    bump.Parameters.AddWithValue("$hi", batchHi);
                    await bump.ExecuteNonQueryAsync(ct);
                }
                if (changed.Count > 0) await WriteChanged(conn, tx, changed, ct);
                if (ownsTx) await tx.CommitAsync(ct);
            }
            finally { if (ownsTx) await tx.DisposeAsync(); }

            last = batchHi;
            if (batchCount < BatchSize) break;
        }
        return new(total, hidden, shown);
    }

    static async Task WriteChanged(SqliteConnection conn, SqliteTransaction tx, List<Post> changed, CancellationToken ct)
    {
        await using var upd = conn.CreateCommand();
        upd.Transaction = tx;
        upd.CommandText = "UPDATE Posts SET Hidden = $h, HiddenBy = $by, HiddenReason = $reason, HiddenAt = $at, VisibilityRevision = $rev, CategoriesJson = $cat WHERE Id = $id";
        var pH = upd.Parameters.Add("$h", SqliteType.Integer);
        var pBy = upd.Parameters.Add("$by", SqliteType.Text);
        var pReason = upd.Parameters.Add("$reason", SqliteType.Text);
        var pAt = upd.Parameters.Add("$at", SqliteType.Text);
        var pRev = upd.Parameters.Add("$rev", SqliteType.Integer);
        var pCat = upd.Parameters.Add("$cat", SqliteType.Text);
        var pId = upd.Parameters.Add("$id", SqliteType.Integer);
        foreach (var p in changed)
        {
            pH.Value = p.Hidden ? 1 : 0;
            pBy.Value = (object?)p.HiddenBy ?? DBNull.Value;
            pReason.Value = (object?)p.HiddenReason ?? DBNull.Value;
            pAt.Value = p.HiddenAt is { } at ? at.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) : DBNull.Value;
            pRev.Value = p.VisibilityRevision;
            pCat.Value = (object?)p.CategoriesJson ?? DBNull.Value;
            pId.Value = p.Id;
            await upd.ExecuteNonQueryAsync(ct);
        }
    }
}
