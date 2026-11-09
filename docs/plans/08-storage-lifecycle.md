# Plan 08: Storage lifecycle and a bounded read path

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

The database has an automatic daily backup and a backup before every migration. Free pages are reclaimed. Operational rows have a retention rule. A feed request touches rows in proportion to the cards it renders, and the owner can page to older posts.

## Problem and evidence

No code in `src` runs VACUUM, ANALYZE, `PRAGMA optimize`, a WAL checkpoint or a backup. `FeedDb.Initialize` (`FeedDb.cs` L47) migrates in every process, including every CLI child, with no backup first. Raws expire after 60 days (`raw_retention_days`), so the database becomes the only copy of posts, verdicts and feedback.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| `feed.db` size | 412 MB |
| Free pages | 67,759 of 100,498 (67%, about 278 MB) |
| `sqlite_stat1` | absent, ANALYZE never ran |
| Backups | one manual copy from 10-25 |
| Runs / RunRequests / TimelineVisits / SweepTargets rows | 931 / 793 / 5,800 / 5,183, none ever deleted |
| RawSnapshots rows | 35,029; prune only flags `Deleted`, rows stay |
| RawDiagnostics rows | 15,690 |

The read path (`FeedQuery.Read`, `src/Feed.Core/Queries/FeedQuery.cs`) per request: a DISTINCT over Posts for known platforms (L21), all Authors into memory (L23), `Identity.Resolve` per preference entry (L35-41), `json_each` over every post's `CategoriesJson` for category membership (L65-68), a window function over all friend posts for rare views (L89-94) plus a C# load of every dated post's `(Id, AuthorId, PostedAt)` for the "why" text (L29-33), six counts (L104-107, L153), then `Take(RenderCap)` (L111) with no cursor. At about 250 posts a day, three years is about 270k posts, and each of these steps grows with that number. `FeedPerformanceTests` uses 24 posts. `measurements.md` says the synthetic benchmark was not repeated after the 10-25 index change.

## Current behavior to keep in mind

- Daily job: `Scheduler.Tick` L102-106 marks Kv `maintenance:last`, then `Maintenance.Prune(raw: true)` and `Prune(raw: false)`, then `Measure` (L173) writes `disk:summary` and `disk:at` by walking media and raw directories.
- Pragmas per connection (`FeedDb.cs` L52): `busy_timeout=30000`, `cache_size=-65536`, `mmap_size=256MB`, `temp_store=MEMORY`. WAL is set at `Initialize`. `synchronous` is the default FULL. `auto_vacuum` is 0.
- `IX_Posts_FeedMembership` (Platform, Hidden, PostedAt, IsSponsored, ContentRevision, VerdictContentRevision, CategoriesJson) and `IX_Posts_AuthorHistory` (AuthorId, PostedAt) exist since 10-25.
- `CategoriesJson` is written at `Processing.cs` L243 and `Management.cs` L274. No `PostCategories` table exists.
- `GET /` takes `view`, `platform`, `hidden=1`, `unsorted=1` (`Program.cs` L33-36). `FeedView.razor` has no older link. `navigation.js` has no infinite scroll.
- Retention config (`Configuration.cs` L24-27): `raw_retention_days` 60, `hidden_media_retention_days` 7, `hidden_video_retention_days` 1, `video_retention_days` 180.
- Tombstones `deleted-post:<sha>` live in Kv (`StorageCleanup.cs` L14, L46) and block re-ingest (`Ingest.cs` L94).
- Spec 5.1 says "migrate at every start". Spec 05 L96 says a feed read "must touch rows only for the cards it renders". RUNBOOK L46-48 tells the operator to back up by hand with `sqlite3 .backup`.

## Target design

### Lifecycle

1. **Daily backup.** The daily job runs `VACUUM INTO 'data/backups/feed-<yyyyMMdd>.db'` under the processing lock, then checks the copy with `PRAGMA integrity_check`. Rotation keeps the last 7 daily and the last 4 Sunday copies. Config `retention.backups_daily` 7 and `retention.backups_weekly` 4. The copy is compact, so the daily cost is one write of the live data (about 134 MB today).
2. **Backup before migration.** `FeedDb.Initialize` checks `GetPendingMigrationsAsync`. With pending migrations it writes `data/backups/pre-<migration name>.db` first. Only one process may migrate: the web host, or the new CLI command `migrate`. A CLI command other than `migrate` that finds pending migrations exits with "database needs migration; start the web host or run migrate". This replaces "migrate at every start".
3. **Space and statistics.** `migrate` runs a one-time `VACUUM` with `auto_vacuum = INCREMENTAL` after the backup. The daily job then runs `PRAGMA incremental_vacuum`, `PRAGMA optimize` and `PRAGMA wal_checkpoint(TRUNCATE)` after prune. Add `PRAGMA synchronous = NORMAL` per connection; WAL mode makes it durable against application crashes, and the daily backup covers power loss.
4. **Operational row retention.** New rules in `Maintenance.Prune`, with config keys and defaults: `retention.runs_days` 90 (Runs and their RunRequests, except rows referenced by a pending like); `retention.raw_rows_days` 30 after the file deletion (RawSnapshots with `Deleted = 1`, cascading RawDiagnostics); `retention.visits_days` 180 (TimelineVisits, always keeping the latest visit per author for coverage); SweepTargets of any cycle older than the previous one; `deleted-post:` tombstones older than `raw_retention_days` (the replay source is gone, so the block is moot). Spec 7.7 "never deletes runs" changes to "deletes runs older than the retention".
5. **Measurement without a walk.** Add `Media.Bytes` set at download. `Measure` sums `RawSnapshots.Bytes` and `Media.Bytes` daily. A full directory walk runs weekly to reconcile and reports a mismatch as an Informational notice.

### Read path

6. **Category membership as rows.** New table `PostCategories(PostId, Category)` with index `(Category, PostId)`, written wherever `CategoriesJson` is written (Processing apply, refilter) through one helper. Category and intersection views join on it. `CategoriesJson` stays as the raw model output for the card trail. Drop `CategoriesJson` from `IX_Posts_FeedMembership`.
7. **Rare rank at write time.** Add `Post.RareRank`: the number of the author's dated posts in the preceding `rare.window_days` including itself. Ingest updates the rank for the author's posts in the affected window (one bounded query per author per ingest). Rare membership becomes `RareRank <= rare.max_posts`. A config change to the rare window or max triggers a full recompute from Manage settings save. The C# history load (L29-33) and the SQL window (L89-94) go away; the "why" text reads `RareRank`.
8. **Author cache.** The web host caches the Authors list and the resolved preference entries keyed by the configuration stamp and a Kv `authors:version` that `IdentityStore` and `Management` bump on any author write. CLI processes read directly as today.
9. **Keyset paging.** `GET /` accepts `before=<PostedAt ticks>-<Id>`. The page renders `RenderCap` cards and an "Older" link with the last card's key when more exist. Stacks and repeat folding operate within a page as today. Counts stay as index-only queries.
10. **Known platforms without a scan.** Replace the DISTINCT over Posts (L21) with the enabled platforms from configuration plus a Kv `platforms:seen` list maintained at ingest.

## Contracts to preserve

- Binding rule 8: the render never writes. The author cache is in-memory; `platforms:seen` and `RareRank` are written by ingest.
- Binding rule 4: no query re-applies filter logic. `PostCategories` and `RareRank` are projections of stored decisions.
- Retention never deletes posts, authors, media rows or feedback (7.7). Likes rows are kept.
- `ui.render_cap` keeps its meaning as the cards per page.
- The operator's manual `.backup` path in the runbook keeps working.

## Implementation sequence

1. Backup and rotation in the daily job, plus the `migrate` command and the pending-migration guard. Tests: a backup file appears and passes integrity_check; the CLI refuses with pending migrations; rotation deletes the oldest.
2. Pragmas and the one-time VACUUM in `migrate`. Test: `auto_vacuum` reads 2 after migrate on a fresh database.
3. Operational retention rules with tests per table. Keep `--dry-run` output lines in the existing style.
4. `Media.Bytes` and the summed `Measure`. Test: summary equals the walk on a synthetic instance.
5. `PostCategories` with backfill migration from `CategoriesJson`; the write helper; category and intersection views on the join. Test: `EXPLAIN QUERY PLAN` for a category view shows no `SCAN Posts`. `ViewTests` pass unchanged.
6. `RareRank` with backfill, ingest update, config-change recompute. `ViewTests.RareWindowIncludesHiddenHistory...` must pass with the same boundary behavior.
7. Author cache with invalidation test (a merge changes the next request's result).
8. Keyset paging: query parameter, link, browser test with and without JavaScript, `navigation.js` prefetch of the older link.
9. Enlarge `FeedPerformanceTests` to a 5,000-post synthetic set and assert a request-count or plan budget, not wall time.

## Spec updates

- `docs/05-data-model.md` 5.1 (migration only by host or `migrate`, pragmas), 5.2 (PostCategories, `Media.Bytes`, `RareRank`, backups folder, Kv keys), new "Operational retention" section, 5.11 budget (paging).
- `docs/07-media.md` 7.7 (runs retention sentence, measurement).
- `docs/10-views.md` 10.3 (RareRank), new paging note.
- `docs/12-scheduler.md` 12.5 (backup, vacuum, optimize, checkpoint, retention order).
- `docs/13-cli.md` (`migrate`, backup lines in `status`).
- `docs/14-web-host.md` 14.3 (`before` parameter), `docs/15-ui.md` (Older link).
- `docs/16-deployment.md` 16.9 and `docs/RUNBOOK.md` backup section (automatic backups, restore steps).
- `docs/04-instance.md` retention keys.
- `measurements.md`: new synthetic benchmark after step 9.

## Verification

- Fast suite plus the tests above. Browser tests for paging.
- Private instance: after `migrate`, file size should fall from 412 MB to about 140 MB. `X-Render-Ms` for the home view before and after steps 5 to 8, recorded in `data/`. A restore drill from a daily backup into a disposable instance renders the same feed.

## Open questions for the reviewer

1. Migration by the web host only breaks the habit of running a CLI command against a fresh checkout first. Acceptable, or should every CLI process back up and migrate under a lock instead?
2. `synchronous = NORMAL`: accept a small power-loss window in exchange for less fsync load on the slow mount?
3. Should tombstones move to their own table now, or stay in Kv with the retention rule?
4. Paging by `before` link only, or also a date jump?
5. Is 90 days the right retention for Runs? Coverage pages read run history for the dot and the summaries.

## Out of scope

Image retention bounds for visible posts (review finding 7), the scheduler tick frequency, LogSink fsync behavior, the multi-process coordination design (review finding 7 of the architecture list).
