using Feed.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
namespace Feed.Core.Infrastructure;

public sealed class FeedDb(InstancePaths paths) : DbContext
{
    public DbSet<Author> Authors => Set<Author>(); public DbSet<AuthorKey> AuthorKeys => Set<AuthorKey>(); public DbSet<Post> Posts => Set<Post>(); public DbSet<Media> Media => Set<Media>(); public DbSet<RawSnapshot> RawSnapshots => Set<RawSnapshot>(); public DbSet<Run> Runs => Set<Run>(); public DbSet<RunRequest> RunRequests => Set<RunRequest>(); public DbSet<Feedback> Feedback => Set<Feedback>(); public DbSet<Like> Likes => Set<Like>(); public DbSet<PlatformState> PlatformStates => Set<PlatformState>(); public DbSet<TimelineVisit> TimelineVisits => Set<TimelineVisit>(); public DbSet<SweepTarget> SweepTargets => Set<SweepTarget>(); public DbSet<Kv> Kv => Set<Kv>();
    public DbSet<RawDiagnostic> RawDiagnostics => Set<RawDiagnostic>();
    public DbSet<CaptureRecoveryAttempt> CaptureRecoveryAttempts => Set<CaptureRecoveryAttempt>();
    protected override void OnConfiguring(DbContextOptionsBuilder b) => b.UseSqlite($"Data Source={paths.Database};Default Timeout=30;Pooling=True").AddInterceptors(new Pragmas());
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Author>().HasIndex(x => new { x.Platform, x.PlatformAuthorId }).IsUnique(); b.Entity<Author>().HasIndex(x => new { x.Platform, x.IsFriend });
        b.Entity<AuthorKey>().HasKey(x => x.Key); b.Entity<AuthorKey>().HasOne<Author>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<Post>().Ignore(x => x.Judged).Ignore(x => x.DisplayText); b.Entity<Post>().HasOne<Author>().WithMany().HasForeignKey(x => x.AuthorId).OnDelete(DeleteBehavior.SetNull);
        b.Entity<Post>().HasIndex(x => new { x.Platform, x.PlatformPostId }).IsUnique(); b.Entity<Post>().HasIndex(x => new { x.Hidden, x.PostedAt }); b.Entity<Post>().HasIndex(x => x.PostedAt);
        // Feed reads must not touch the wide Posts rows (captions, verdicts, manifests) except for the
        // cards they render. These two indexes cover every membership test, count, ordered id selection
        // and rare-history scan: SQLite answers them from the index alone, so a 10,000-post table costs
        // milliseconds even on a slow mount. Platform leads because every feed scope filters on it with
        // an equality; a leading Hidden would not be searched (EF renders !Hidden as NOT, not = 0).
        b.Entity<Post>().HasIndex(x => new { x.Platform, x.Hidden, x.PostedAt, x.IsSponsored, x.ContentRevision, x.VerdictContentRevision, x.CategoriesJson }).HasDatabaseName("IX_Posts_FeedMembership");
        b.Entity<Post>().HasIndex(x => new { x.AuthorId, x.PostedAt }).HasDatabaseName("IX_Posts_AuthorHistory");
        b.Entity<Media>().HasOne<Post>().WithMany().HasForeignKey(x => x.PostId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RawDiagnostic>().HasOne<RawSnapshot>().WithMany().HasForeignKey(x => x.RawSnapshotId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RawDiagnostic>().HasIndex(x => new { x.Fingerprint, x.RawSnapshotId });
        b.Entity<CaptureRecoveryAttempt>().HasOne<RawSnapshot>().WithMany().HasForeignKey(x => x.RawSnapshotId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<CaptureRecoveryAttempt>().HasIndex(x => new { x.RawSnapshotId, x.StartedAt });
        b.Entity<RawSnapshot>().HasIndex(x => x.RecoveryKey);
        b.Entity<RawSnapshot>().HasIndex(x => x.Path).IsUnique(); b.Entity<RawSnapshot>().HasIndex(x => new { x.Platform, x.CapturedAt });
        b.Entity<Run>().HasIndex(x => new { x.Platform, x.Status }); b.Entity<Run>().HasIndex(x => x.RequestId); b.Entity<Run>().HasIndex(x => x.FinishedAt);
        b.Entity<RunRequest>().HasIndex(x => new { x.Kind, x.Platform }).IsUnique().HasFilter("Status IN ('pending','claimed') AND Kind IN ('collect','like','friends','login')");
        b.Entity<RunRequest>().HasIndex(x => x.Kind).IsUnique().HasFilter("Status IN ('pending','claimed') AND Kind = 'process'"); b.Entity<RunRequest>().HasIndex(x => new { x.Status, x.RequestedAt });
        b.Entity<RunRequest>().ToTable(t => t.HasCheckConstraint("CK_RequestScope", "(Kind = 'process' AND Platform IS NULL) OR (Kind IN ('collect','like','friends','login') AND Platform IN ('facebook','instagram'))"));
        b.Entity<Like>().HasIndex(x => new { x.PostId, x.State }); b.Entity<Like>().HasIndex(x => x.PostId).IsUnique().HasFilter("State = 'pending'"); b.Entity<Feedback>().HasIndex(x => x.PostId);
        b.Entity<Post>().HasIndex(x => new { x.Platform, x.LlmAttemptedAt, x.CapturedAt }).HasDatabaseName("IX_Posts_JudgeQueue").HasFilter("IngestReadyAt IS NOT NULL AND (Hidden = 0 OR HiddenBy = 'llm') AND (CategoriesJson IS NULL OR VerdictContentRevision IS NULL OR VerdictContentRevision <> ContentRevision OR LlmTokenLimit IS NOT NULL)");
        b.Entity<Post>().HasIndex(x => new { x.Platform, x.SummaryAttemptedAt, x.CapturedAt }).HasDatabaseName("IX_Posts_SummaryQueue").HasFilter("IngestReadyAt IS NOT NULL AND Hidden = 0 AND (Summary IS NULL OR SummaryContentRevision IS NULL OR SummaryContentRevision <> ContentRevision)");
        b.Entity<Media>().HasIndex(x => new { x.Kind, x.IsCurrent, x.AttemptedAt, x.CreatedAt }).HasFilter("Path IS NULL AND PrunedAt IS NULL");
        b.Entity<RawSnapshot>().HasIndex(x => x.CaptureToken);
        b.Entity<TimelineVisit>().HasIndex(x => x.CaptureToken);
        b.Entity<RawSnapshot>().HasIndex(x => new { x.Platform, x.AttemptedAt, x.CapturedAt }).HasFilter("Parsed = 0 AND Deleted = 0");
        b.Entity<PlatformState>().HasKey(x => x.Platform); b.Entity<TimelineVisit>().HasIndex(x => new { x.Platform, x.Url, x.Id });
        b.Entity<SweepTarget>().HasKey(x => new { x.Platform, x.CycleId, x.AuthorId }); b.Entity<Kv>().HasKey(x => x.Key);
    }
    public async Task Initialize(CancellationToken ct = default) { await Database.MigrateAsync(ct); await Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct); await Application.MediaKeys.Normalize(this, paths, ct); }
    public async Task Put(string key, string value, CancellationToken ct = default) => await Database.ExecuteSqlInterpolatedAsync($"INSERT INTO Kv (Key,Value) VALUES ({key},{value}) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ct);
    public async Task<string?> Get(string key, CancellationToken ct = default) => await Kv.Where(x => x.Key == key).Select(x => x.Value).FirstOrDefaultAsync(ct);
    sealed class Pragmas : DbConnectionInterceptor
    {
        static void Apply(DbConnection connection) { using var cmd = connection.CreateCommand(); cmd.CommandText = "PRAGMA busy_timeout=30000; PRAGMA cache_size=-65536; PRAGMA mmap_size=268435456; PRAGMA temp_store=MEMORY;"; cmd.ExecuteNonQuery(); }
        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);
        public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) { Apply(connection); return Task.CompletedTask; }
    }
}
public sealed class DbFactory(InstancePaths paths) { public InstancePaths Paths => paths; public FeedDb Open() => new(paths); }
