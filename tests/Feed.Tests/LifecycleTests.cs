using Feed.Core.Application;
using Feed.Core.Domain;
using Feed.Core.Infrastructure;
using Feed.Web;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
namespace Feed.Tests;
public sealed class LifecycleTests
{
    static async Task<string> Scalar(string file, string sql)
    {
        await using var connection = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Pooling=False"); await connection.OpenAsync();
        using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(await command.ExecuteScalarAsync())!;
    }
    [Fact] public async Task BackupWritesACheckedCopyAndRecordsIt()
    {
        await using var i = new TestInstance(); await i.Init(); var maintenance = new Maintenance(i.Paths, i.Factory, new HttpClient());
        await using (var db = i.Factory.Open()) { db.Add(new Author { Platform = "instagram", PlatformAuthorId = "1", DisplayName = "Synthetic" }); await db.SaveChangesAsync(); }
        var result = await maintenance.Backup(default); var file = i.Paths.Get("backups", $"feed-{Clock.Now:yyyyMMdd}.db");
        Assert.StartsWith("backup: feed-", result); Assert.True(File.Exists(file)); Assert.Empty(Directory.GetFiles(i.Paths.Get("backups"), "*.tmp"));
        Assert.Equal("ok", await Scalar(file, "PRAGMA integrity_check")); Assert.Equal("1", await Scalar(file, "SELECT COUNT(*) FROM Authors"));
        await using (var db = i.Factory.Open()) { Assert.NotNull(await db.Get("backup:last")); Assert.Null(await db.Get("backup:error")); Assert.Contains("backup: last", await Maintenance.BackupLine(i.Paths, db, default)); Assert.Contains("1 copies", await Maintenance.BackupLine(i.Paths, db, default)); }
        // A second run on the same day keeps the existing copy instead of rewriting it.
        var written = File.GetLastWriteTimeUtc(file); await Task.Delay(20); await maintenance.Backup(default); Assert.Equal(written, File.GetLastWriteTimeUtc(file));
    }
    [Fact] public async Task BackupFailureIsRecordedAndShown()
    {
        await using var i = new TestInstance(); await i.Init(); await File.WriteAllTextAsync(i.Paths.Get("backups"), "not a folder");
        var result = await new Maintenance(i.Paths, i.Factory, new HttpClient()).Backup(default); Assert.StartsWith("backup failed", result);
        await using var db = i.Factory.Open(); Assert.NotNull(await db.Get("backup:error")); Assert.StartsWith("backup: FAILED", await Maintenance.BackupLine(i.Paths, db, default));
    }
    [Fact] public async Task SnapshotRefusesTheLiveFileAndKeepsAnExistingTarget()
    {
        await using var i = new TestInstance(); await i.Init(); await using var db = i.Factory.Open();
        await Assert.ThrowsAsync<IOException>(() => Maintenance.Snapshot(db, i.Paths.Database, default));
        await Assert.ThrowsAsync<IOException>(() => Maintenance.Snapshot(db, i.Paths.Get("..", Path.GetFileName(i.Paths.Root), "feed.db"), default));
        var target = i.Paths.Get("backups", "pre-existing.db"); Directory.CreateDirectory(i.Paths.Get("backups")); await File.WriteAllTextAsync(target, "keep");
        await Maintenance.Snapshot(db, target, default); Assert.Equal("keep", await File.ReadAllTextAsync(target));
    }
    [Fact] public void RotationKeepsSevenDailyFourSundayAndTwoPreMigrationCopies()
    {
        var folder = Path.Combine(Path.GetTempPath(), "feed-rotate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        try
        {
            var last = new DateTime(2026, 11, 10); // a Tuesday
            for (var d = 0; d < 40; d++) File.WriteAllText(Path.Combine(folder, $"feed-{last.AddDays(-d):yyyyMMdd}.db"), "");
            foreach (var name in new[] { "pre-20261001000000_A.db", "pre-20261101000000_B.db", "pre-20261110000000_C.db" }) File.WriteAllText(Path.Combine(folder, name), "");
            File.WriteAllText(Path.Combine(folder, "feed-manual.db"), "");
            Maintenance.Rotate(folder);
            var kept = Directory.GetFiles(folder).Select(Path.GetFileName).Order().ToArray();
            var expected = Enumerable.Range(0, 7).Select(d => $"feed-{last.AddDays(-d):yyyyMMdd}.db")
                .Concat(new[] { "feed-20261108.db", "feed-20261101.db", "feed-20261025.db", "feed-20261018.db" })
                .Concat(new[] { "pre-20261101000000_B.db", "pre-20261110000000_C.db", "feed-manual.db" }).Distinct().Order().ToArray();
            Assert.Equal(expected, kept);
        }
        finally { Directory.Delete(folder, true); }
    }
    [Fact] public async Task InitializeBacksUpBeforeAPendingMigrationAndNotOnAFreshInstance()
    {
        await using var i = new TestInstance(); await i.Init(); Assert.False(Directory.Exists(i.Paths.Get("backups")));
        await using (var db = i.Factory.Open())
        {
            var all = db.Database.GetMigrations().ToArray(); await db.GetService<IMigrator>().MigrateAsync(all[^2]);
            Assert.Single(await db.Database.GetPendingMigrationsAsync());
            await db.Initialize();
            Assert.Empty(await db.Database.GetPendingMigrationsAsync()); var file = i.Paths.Get("backups", $"pre-{all[^1]}.db"); Assert.True(File.Exists(file));
            // The copy is the state before the migration: its history table lacks the last migration.
            Assert.Equal("0", await Scalar(file, $"SELECT COUNT(*) FROM __EFMigrationsHistory WHERE MigrationId = '{all[^1]}'"));
            var written = File.GetLastWriteTimeUtc(file); await db.Initialize(); Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        }
    }
    [Fact] public async Task CompactConvertsOnceAfterABackupThenVacuumsIncrementally()
    {
        await using var i = new TestInstance(); await i.Init(); var maintenance = new Maintenance(i.Paths, i.Factory, new HttpClient());
        Assert.Contains("waits for a backup", await maintenance.Compact(default)); Assert.Equal("0", await Scalar(i.Paths.Database, "PRAGMA auto_vacuum"));
        await maintenance.Backup(default);
        Assert.Contains("converted", await maintenance.Compact(default));
        Assert.Equal("2", await Scalar(i.Paths.Database, "PRAGMA auto_vacuum")); Assert.Equal("1", await Scalar(i.Paths.Database, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'sqlite_stat1'"));
        Assert.Contains("incremental vacuum", await maintenance.Compact(default));
        using (ResourceLock.Try(i.Paths, "ingest-facebook")) await Assert.ThrowsAsync<ResourceBusyException>(() => maintenance.Compact(default));
    }
    [Fact] public async Task TrimDeletesOnlyOldDeletedRawRowsWithTheirDiagnostics()
    {
        await using var i = new TestInstance(); await i.Init(); var maintenance = new Maintenance(i.Paths, i.Factory, new HttpClient());
        await using (var db = i.Factory.Open())
        {
            var old = new RawSnapshot { Platform = "facebook", Path = "old.json", Parsed = true, Deleted = true, CapturedAt = Clock.Now.AddDays(-200) };
            var recent = new RawSnapshot { Platform = "facebook", Path = "recent.json", Parsed = true, Deleted = true, CapturedAt = Clock.Now.AddDays(-10) };
            var live = new RawSnapshot { Platform = "facebook", Path = "live.json", Parsed = true, CapturedAt = Clock.Now.AddDays(-200) };
            db.AddRange(old, recent, live); await db.SaveChangesAsync();
            db.Add(new RawDiagnostic { RawSnapshotId = old.Id, Code = "a", Fingerprint = "f1", Impact = "auxiliary" }); db.Add(new RawDiagnostic { RawSnapshotId = live.Id, Code = "b", Fingerprint = "f2", Impact = "auxiliary" }); await db.SaveChangesAsync();
        }
        var config = new InstanceFiles(i.Paths).Current.Config; Assert.Equal(180, config.RawRowsRetentionDays);
        Assert.Equal("trim: dry-run deleted raw rows=1, diagnostics=1; rows deleted more than 180 days ago by capture date", await maintenance.Trim(config, true, default));
        await using (var db = i.Factory.Open()) { Assert.Equal(3, await db.RawSnapshots.CountAsync()); Assert.Equal(2, await db.RawDiagnostics.CountAsync()); }
        Assert.StartsWith("trim: done deleted raw rows=1, diagnostics=1", await maintenance.Trim(config, false, default));
        await using (var db = i.Factory.Open()) { Assert.Equal(["live.json", "recent.json"], (await db.RawSnapshots.Select(r => r.Path).ToListAsync()).Order()); Assert.Equal("b", (await db.RawDiagnostics.SingleAsync()).Code); }
        Assert.Equal("trim: raw rows retention is off", await maintenance.Trim(config with { RawRowsRetentionDays = 0 }, false, default));
        using (ResourceLock.Try(i.Paths, "processing")) await Assert.ThrowsAsync<ResourceBusyException>(() => maintenance.Trim(config, false, default));
    }
    [Fact] public async Task DailyMaintenanceBacksUpThenCompacts()
    {
        await using var i = new TestInstance(); await i.Init();
        await File.WriteAllTextAsync(i.Paths.Get("config.json"), """{"platforms":{},"scheduler":{"enabled":true},"backgrounds":{"source":"off"}}""");
        var scheduler = new Scheduler(i.Paths, new InstanceFiles(i.Paths), i.Factory, NullLogger<Scheduler>.Instance, () => throw new Exception("nothing to launch"));
        await scheduler.Tick(default);
        Assert.True(File.Exists(i.Paths.Get("backups", $"feed-{Clock.Now:yyyyMMdd}.db"))); Assert.Equal("2", await Scalar(i.Paths.Database, "PRAGMA auto_vacuum"));
        await using var db = i.Factory.Open(); Assert.Equal(Clock.Now.ToString("yyyy-MM-dd"), await db.Get("maintenance:last")); Assert.Contains("backups", await db.Get("disk:summary"));
    }
}
