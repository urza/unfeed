# Plan 08: Storage lifecycle

Status: reviewed 2026-11-10 (`.work/08-storage-lifecycle/plan-review.md`). The step-back verdict (CHANGE COURSE) and the owner-proxy decisions reduced the scope to the lifecycle: daily backup, backup before migration, one-time VACUUM with incremental auto-vacuum, retention for deleted raw rows, and two bounded reads. No `migrate` command, no `RareRank`, no `PostCategories`, no author cache, no `Media.Bytes`, no tombstone expiry, no `synchronous = NORMAL`, no paging. Ready to implement.

## Outcome

The database has an automatic daily backup with an integrity check and rotation, and a backup before every migration. Free pages are reclaimed once, then daily. Deleted raw rows have a retention rule. The Manage overview and the feed no longer read whole tables for a per-platform fact.

## Problem and evidence

No code in `src` runs VACUUM, ANALYZE, `PRAGMA optimize`, a WAL checkpoint or a backup. `FeedDb.Initialize` (`FeedDb.cs` L45) migrates in every process, including every CLI child, with no backup first. Raws expire after 60 days (`raw_retention_days`), so the database becomes the only copy of posts, verdicts and feedback.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| `feed.db` size | 412 MB |
| Free pages | 67,759 of 100,498 (67%, about 278 MB) |
| `sqlite_stat1` | absent, ANALYZE never ran |
| Backups | one manual copy from 10-25 |
| Runs / RunRequests / TimelineVisits / SweepTargets rows | 931 / 793 / 5,800 / 5,183, none ever deleted |
| RawSnapshots rows | 35,029 in six weeks; prune only flags `Deleted`, rows stay |
| RawDiagnostics rows | 15,690 |

The raw tables carry the volume: about 800 snapshot rows a day, with their diagnostics. The run tables are small. The Manage overview loads every run (`Management.cs` L59) to find the latest collect and friends run per platform. The feed read runs a DISTINCT over the whole Posts index for the known platforms (`FeedQuery.cs` L21).

## Current behavior to keep in mind

- Daily job: `Scheduler.Tick` L100-104 marks Kv `maintenance:last`, then `Maintenance.Prune(raw: true)` and `Prune(raw: false)`, each under the processing lock and the ingest locks, then `Measure` (L174) writes `disk:summary` and `disk:at` by walking media and raw directories. A busy lock defers the whole job to the next day.
- Pragmas per connection (`FeedDb.cs` L50): `busy_timeout=30000`, `cache_size=-65536`, `mmap_size=256MB`, `temp_store=MEMORY`. WAL is set at `Initialize`. `synchronous` is the default FULL. `auto_vacuum` is 0.
- EF Core 10 serializes `MigrateAsync` across processes with its migration lock, so parallel starts do not race on the schema.
- `MediaKeys.Normalize` (plan 03) runs once at `Initialize` and returns early on its marker.
- Retention config (`Configuration.cs` L26-29): `raw_retention_days` 60, `hidden_media_retention_days` 7, `hidden_video_retention_days` 1, `video_retention_days` 180. Keys are top-level snake_case.
- Tombstones `deleted-post:<sha>` live in Kv (`StorageCleanup.cs` L14, L46) with value `"1"`. `Ingest.Store` (`Ingest.cs` L95) checks them on every observation, live captures included.
- Capped and checkpoint runs keep their raw files for `2 × raw_retention_days` (`Maintenance.cs` L130, L145). `RawDiagnostics` cascade from `RawSnapshots`. `CaptureHealth.CleanVisits` joins recent visits to raw rows by `CaptureToken`; the deleted rows it needs are days old, not months.
- Spec 5.1 says "migrate at every start". RUNBOOK L46-48 tells the operator to back up by hand with `sqlite3 .backup`. `docs/15-ui.md` L76 and `docs/04-instance.md` L150 say the feed has no paging: `render_cap` is the budget.
- The bundled SQLite is 3.53.3 (`SQLitePCLRaw.lib.e_sqlite3` 2.1.13), so `PRAGMA optimize=0x10002` is available.

## Target design

1. **Daily backup.** The daily job runs `VACUUM INTO '<instance>/backups/feed-<yyyyMMdd>.db'` before prune, with no lock: `VACUUM INTO` reads one snapshot and never blocks a writer. The copy is written to a temporary name and renamed when `PRAGMA integrity_check` on the copy returns `ok`. Rotation keeps the last 7 daily copies and the last 4 copies that fell on a Sunday; both numbers are constants in `Maintenance`, not config. Kv `backup:last` holds the time of the last good copy; Kv `backup:error` holds the last failure text and is cleared by the next good copy. A failed backup is logged, shown in `status` and on the Manage overview next to the disk line, and retried on the next day. `disk:summary` gains the size of `backups/`. The target file is never the live database: the helper refuses a path equal to `paths.Database`.
2. **Backup before migration.** `FeedDb.Initialize` calls `GetPendingMigrationsAsync`. When migrations are pending and the database already has applied migrations (not a fresh instance), it writes `backups/pre-<first pending migration>.db` through the same helper, then migrates as today. An existing file of that name is kept, so the second process of a parallel start skips the copy. The last 2 `pre-*` files are kept. Every process still migrates; the EF migration lock serializes them.
3. **Space and statistics.** A new `Maintenance.Compact` runs in the daily job after prune, under the processing lock and all ingest locks. When `PRAGMA auto_vacuum` is not 2 and today's backup succeeded, it runs `PRAGMA auto_vacuum = INCREMENTAL; VACUUM; ANALYZE;` once and logs the duration and the size before and after. Otherwise it runs `PRAGMA incremental_vacuum`. Then it always runs `PRAGMA wal_checkpoint(TRUNCATE)` and `PRAGMA optimize=0x10002` (the plain `optimize` only looks at tables the same connection queried). `synchronous` stays FULL.
4. **Deleted raw row retention.** One config key `raw_rows_retention_days` (default 180, 0 never). `Maintenance.Trim(c, dryRun, ct)`, under the processing lock, deletes `RawSnapshots` with `Deleted = 1` and `CapturedAt` before the cutoff; their `RawDiagnostics` cascade. No other table is trimmed: runs, requests, visits and sweep targets are small, and a rule for them needs an ordering invariant with the raw prune (review finding 2). The daily job runs `Trim` after prune and before `Compact`. The CLI gets `trim [--dry-run]` with one summary line in the prune style.
5. **Bounded reads.** The Manage overview reads the latest collect run, the latest friends run and the running runs per platform with per-platform queries on `IX_Runs_Platform_Status` instead of loading every run. The feed read replaces the DISTINCT over Posts with `Platforms.All.Where(p => db.Posts.Any(x => x.Platform == p))`: two index probes.

## Contracts to preserve

- Binding rule 8: the render never writes.
- Retention never deletes posts, authors, media rows, feedback, likes, runs, visits, identity events or tombstones.
- `ui.render_cap` keeps its meaning; the feed stays a single finite page.
- The operator's manual `.backup` path in the runbook keeps working.
- No code path writes to, deletes or vacuums the live database file as a target: `VACUUM INTO` writes a new file, `VACUUM` rewrites in place through SQLite itself.

## Implementation sequence

1. `Maintenance.Backup` (VACUUM INTO, integrity check, rename, rotation, Kv result) and its call in the daily job before prune. Tests on a `TestInstance`: a backup file appears and passes `integrity_check`; a copy that fails the check is not renamed and sets `backup:error`; rotation keeps 7 daily and 4 Sunday files from a synthetic set of names; the helper refuses the live path.
2. Pre-migration backup in `FeedDb.Initialize`. Test: a database at an older migration gets a `pre-*.db` file before the schema changes; a fresh instance gets none.
3. `Maintenance.Compact`. Test: on a fresh database `auto_vacuum` reads 2 and `sqlite_stat1` exists after the first `Compact`; the second call takes the incremental path.
4. `raw_rows_retention_days` and `Maintenance.Trim` with a test and a dry-run test; CLI `trim`.
5. `disk:summary` with the backups size; `status` and the Manage overview show the backup line.
6. Bounded reads: the Manage overview runs and the known platforms probe.

## Spec updates

- `docs/05-data-model.md` 5.1 (backup before migration, every process migrates), 5.2 (backups folder, Kv keys `backup:last`, `backup:error`), new "Operational retention" section.
- `docs/07-media.md` 7.7 (deleted raw row retention, backups in the disk summary).
- `docs/12-scheduler.md` 12.5 (order: backup, prune, trim, compact).
- `docs/13-cli.md` (`trim`, backup line in `status`).
- `docs/16-deployment.md` 16.9 and `docs/RUNBOOK.md` backup section (automatic backups, restore steps, copy `backups/` off the host).
- `docs/04-instance.md` retention key.

## Verification

- Fast suite plus the tests above.
- On a copy of the live database under the scratchpad (never on `data/`): `Backup` then `Compact`; the file size should fall from 412 MB to about 140 MB; `integrity_check` returns `ok` on the result; the feed renders the same first page before and after.
- A restore drill from a daily backup into a disposable instance renders the same feed.

## Open questions, answered at review

1. Migration by the web host only: **no**. Every process migrates, as today; the EF migration lock serializes them. The backup before migration is what the owner needs.
2. `synchronous = NORMAL`: **no**. The feed holds verdicts and feedback that no raw replay can restore after 60 days. The fsync load was never measured.
3. Tombstones stay in Kv with no retention. They block live re-ingest, not only replay.
4. Paging: **dropped**. Spec 15 and 04 state "no paging" as a product rule; `render_cap` can be raised to show older posts. Only the owner can reverse that rule.
5. Runs retention: **dropped** in favor of bounded reads. The run tables are small; deleted raw rows are the volume and get the one rule.

## Out of scope

Image retention bounds for visible posts (review finding 7), the scheduler tick frequency, LogSink fsync behavior, the multi-process coordination design (review finding 7 of the architecture list). Off-host backup copies (the runbook tells the owner to copy `backups/`). The coverage read of all timeline visits: its logic needs the per-author history, and the table is small.

Dropped at plan review, with the reason in `.work/08-storage-lifecycle/plan-review.md`: `PostCategories` (the covering index already answers the category test), `RareRank` (per-view rare settings, author merges and the "why" text all break a stored rank), the author cache (no measured cost), `Media.Bytes` with a weekly reconcile walk (the sums can never match the walk), `platforms:seen` in Kv (two index probes do it), the 5,000-post benchmark step, the `migrate` command and the pending-migration guard (fresh installs would refuse `init`; Docker owners would never get the VACUUM), the tombstone expiry (it would bring deleted posts back), `synchronous = NORMAL`, the retention config keys for the backup rotation, keyset paging (a product rule says no paging), and retention for runs, requests, visits and sweep targets (small tables, an ordering invariant with the raw prune).
