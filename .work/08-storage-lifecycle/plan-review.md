# Round 1 (2026-11-10)

Target: `docs/plans/08-storage-lifecycle.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: tables that grow for years, a VACUUM and a backup on a slow mount, read-path queries), ux (reason: a new CLI command, a refusal message, an Older link, the runbook). Security was left out: the only new input was the owner's `before` query parameter, and the backups are written, never read, by the app.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: CHANGE COURSE

Why: The backup, the pre-migration copy and the one-time VACUUM fix a real risk, because after 60 days the database is the only copy. Trim and paging have weak drivers and add the most risk.

Instead:
- Keep steps 1, 2, 3, 5 and 7.
- Replace Trim (step 4) with bounded reads. The cited problem is two unbounded reads: `Management.cs` L59 loads all runs, and `CoverageQuery` loads all visits. A `Take(n)` on the runs and a "latest visit per author" query fix that. The tables hold 931 runs and 5,800 visits, which is trivial. Trim needs five ordered rules and a reference-ordering invariant with the raw prune, and that invariant already failed once (finding 2).
- Drop keyset paging (step 6). `docs/15-ui.md` L76 and `docs/04-instance.md` L150 say "No paging" as a product requirement. `render_cap` can go up to 100000 to show older posts. Paging also splits stacks and needs a null-date key. Only the owner can reverse this rule, and an owner proxy should not.

User (owner proxy, 2026-11-10): accepted with one adjustment. Paging is dropped; the spec rule stands until the owner changes it. The run, request, visit and sweep-target rules are dropped, and the Manage overview reads runs per platform instead of all of them. The coverage read stays as it is: its logic needs each author's visit history (latest success, run of incomplete visits), and a "latest visit per author" query would change what the page says. One retention rule stays, for deleted raw rows only: those tables carry the volume (35k snapshot rows and 15k diagnostics in six weeks, about 800 rows a day), the owner named the raw tables as a priority, and the rule has no ordering invariant with the prune because it deletes rows whose files are already gone.

## Skeptic's cons

1. **The plan joins two unrelated projects into one nine-step sequence.** (Outcome; Implementation sequence.) The urgent part is the backup, because the database is the only copy after 60 days. That part waits behind the read path rewrite. The rewrite rests on a projection to 270k posts, and the plan gives no measured slow render.
   Answer: Agree: the read path keeps only paging and the platform probe. `PostCategories`, `RareRank` and the author cache are gone (findings 3, 4, 5). The lifecycle steps come first.
   User (owner proxy, 2026-11-10): accepted. The lifecycle is the owner's priority; the read path is second.

2. **The tombstone retention can let deleted posts come back.** (Lifecycle 4, "the block is moot".) Tombstones block re-ingest from live sweeps, not only from replay. A platform that still serves an old post brings the deleted post back.
   Answer: Agree: `Ingest.Store` (`Ingest.cs` L95) checks the tombstone on every observation. The value is `"1"`, so there is also no date to expire. The rule is removed (finding 1).
   User (owner proxy, 2026-11-10): removed.

3. **`RareRank` stores a value that depends on other rows, and only ingest and config saves update it.** (Read path 7.) Author merges, post deletes, hidden changes and backdated ingest all change the ranks. Rare views then go wrong with no error.
   Answer: Agree: `IdentityStore.cs` L36 moves posts between authors on a merge, and rare settings are per view (`Configuration.cs` L108), so one column cannot serve two rare views. `RareRank` is removed (finding 4).
   User (owner proxy, 2026-11-10): removed.

4. **The backups go to `data/backups` on the same disk as `feed.db`.** (Lifecycle 1.) A disk failure or a lost mount removes the live file and all copies. Eleven copies of about 134 MB is about 1.5 GB on the slow mount.
   Answer: Agree in part: the same-disk copy still covers the common cases (a bad migration, a corrupt write, an operator mistake). An off-host copy is a runbook step, not app code. The disk summary now counts `backups/` so the owner sees the size (finding 9).
   User (owner proxy, 2026-11-10): keep the same-disk copies; the runbook tells the owner to copy `backups/` off the host. Off-host sync stays out of scope.

5. **Keyset paging on `PostedAt` does not fit the sorted views or stacks.** (Read path 9.) `unsorted=1` implies a default order that is not by date, so `before=<ticks>-<Id>` can skip or repeat cards. A stack that crosses a page border splits into two parts.
   Answer: Disagree on the order: all three scopes order by `PostedAt DESC, Id DESC` (`FeedQuery.cs` L109-110); "unsorted" means "not yet judged", not an order. Agree on the stack split; the plan now states it, and the null-date key is defined (finding 7).
   User (owner proxy, 2026-11-10): paging is dropped after the step-back verdict. The spec says the feed has no paging (`docs/15-ui.md` L76).

6. **"Only one process may migrate" is a convention, and no lock enforces it.** (Lifecycle 2.) A web host that starts while CLI children run, or a `migrate` that runs next to the host, can race. The pre-migration backup of a 412 MB file also slows host startup.
   Answer: Agree: the rule is dropped (finding 3). EF Core 10 serializes `MigrateAsync` with its migration lock, so every process keeps migrating as today. The pre-migration backup is a `VACUUM INTO` of the live data (about 134 MB), once per migration, and the host start is the right place to pay it.
   User (owner proxy, 2026-11-10): dropped the rule, kept the backup.

7. **`synchronous = NORMAL` uses the daily backup as cover for power loss.** (Lifecycle 3.) A power loss can then cost up to one day of verdicts and feedback. The plan calls this "a small window". It does not measure the fsync load that it wants to reduce.
   Answer: Agree: no measurement exists. The change is dropped (finding 8).
   User (owner proxy, 2026-11-10): dropped. `synchronous` stays FULL.

## Findings

### 1. Tombstone expiry brings deleted posts back: Major, FIX
Reviewers: neutral, minimalist, ux, skeptic. Where: Lifecycle 4; `Ingest.cs` L95; `StorageCleanup.cs` L46
Finding: The tombstone check runs on every ingest observation, live captures included. A profile sweep that sees a deleted post after 60 days ingests it again, judges it again and downloads its media again. The value `"1"` has no date to compare with.
Resolution: The rule is removed. Open question 3 is answered: tombstones stay in Kv with no retention.

### 2. Runs retention breaks raw retention for capped and checkpoint runs: Major, FIX
Reviewers: neutral. Where: Lifecycle 4; `Maintenance.cs` L130, L145
Finding: Raw files of capped and checkpoint runs live 120 days, but the plan deleted their runs at 90. `Prune` then uses the 60-day cutoff for the snapshot and skips the folder for good (`run is null → continue`).
Resolution: The run rule is gone (step-back verdict). The one rule left deletes `RawSnapshots` with `Deleted = 1` older than `raw_rows_retention_days` (180); their files were deleted by the prune before, so no run lookup depends on them. The "pending like" exception is gone: likes do not reference runs.

### 3. A single migrator is not needed, and it breaks fresh installs and Docker upgrades: Blocker, FIX
Reviewers: ux (Blocker), minimalist, neutral, skeptic. Where: Lifecycle 2 and 3; `Program.cs` L62; `docs/16-deployment.md` L59, L138
Finding: On a fresh instance every migration is pending, so the documented first step `init` would refuse. A Docker owner upgrades with `docker compose up -d`, so the web host migrates and the one-time VACUUM in `migrate` never runs. EF Core 10 already serializes `MigrateAsync` across processes with its migration lock.
Resolution: No `migrate` command and no guard. `Initialize` writes the `pre-*` backup when migrations are pending and the database already has tables, then migrates as today. The one-time VACUUM moves into the daily `Compact`, where the locks already exist and every deployment reaches it. The ux nit about an exit code for the refusal is moot.

### 4. `RareRank` cannot serve per-view rare settings, merges or the "why" text: Major, FIX
Reviewers: neutral, ux, minimalist, performance, skeptic. Where: Read path 7; `Configuration.cs` L108, L185; `IdentityStore.cs` L36
Finding: `Rare` belongs to each view, so two rare views with different windows need different ranks. A merge moves posts between authors with no recompute. The "why" text needs the previous post's date, which the rank does not hold. The full recompute would rewrite every post inside a web request.
Resolution: Item 7 and step 6 are removed. The SQL window over `IX_Posts_AuthorHistory` stays.

### 5. The read-path rewrite has no measured cost behind it: Major, FIX
Reviewers: minimalist, performance. Where: Read path 6, 8, 10; Implementation step 9
Finding: `CategoriesJson` sits in the covering index, so `json_each` reads the index, not the row. The author cache adds a Kv version and two invalidation writers for a load nobody timed. `platforms:seen` needs a backfill and an ingest hook, where two `Any` probes do the same.
Resolution: `PostCategories`, the author cache and `platforms:seen` are removed. Item 10 becomes `Platforms.All.Where(p => Posts.Any(...))`. Step 9 (a 5,000-post benchmark with a plan budget) is removed; the paging test joins `FeedPerformanceTests`. The Outcome text now says "the selection is bounded by the page" instead of "touches rows in proportion to the cards" (performance nit 5: the six counts stay index-only scans).

### 6. The daily backup is skipped whenever processing is busy: Major, FIX
Reviewers: neutral. Where: Lifecycle 1; `Scheduler.cs` L99-104
Finding: `Tick` writes `maintenance:last` and then dispatches processing, so a child often holds the processing lock when `Prune` runs. A backup under that lock would be skipped that day.
Resolution: The backup runs first and takes no lock. `VACUUM INTO` reads one snapshot. The one-time VACUUM, which does need the locks, runs in `Compact` only after a successful backup that day.

### 7. Keyset paging ignored a null `PostedAt` and did not keep the scope: Major, FIX
Reviewers: neutral, ux. Where: Read path 9
Finding: `PostedAt` is nullable, so `before=<ticks>-<Id>` could not encode a card without a date; posts after it would disappear or repeat. An Older link that drops `hidden=1` or `view` opens the wrong feed.
Resolution: Paging is dropped after the step-back verdict, so the finding is moot. The plan records the spec rule that ended it.

### 8. `synchronous = NORMAL` and the incremental vacuum had no measured need: Major, FIX in part
Reviewers: minimalist, skeptic. Where: Lifecycle 3
Finding: `synchronous = NORMAL` trades durability for an fsync load nobody measured. The minimalist also proposes a manual VACUUM in the runbook instead of auto-vacuum and the daily checkpoint.
Resolution: `synchronous` stays FULL (open question 2). The auto-vacuum conversion stays: the owner asked for it, 67% of the file is free pages today, and a runbook step would not run on the Docker host. The daily `incremental_vacuum`, checkpoint and `optimize` are three pragma lines under locks that already exist.

### 9. Backup failures were silent and backup files were not measured: Major, FIX
Reviewers: ux, performance. Where: Lifecycle 1, 2 and 5
Finding: The plan did not say what the owner sees when `VACUUM INTO` or the check fails. `pre-*` files had no rotation. `Measure` left out `backups/` (about 1.5 GB today, more as the database grows).
Resolution: Kv `backup:last` and `backup:error`, shown in `status` and on the Manage overview. The last 2 `pre-*` files are kept. `disk:summary` counts `backups/`.

### 10. `PRAGMA optimize` on a fresh connection never creates statistics: Major, FIX
Reviewers: performance. Where: Lifecycle 3
Finding: The default `optimize` only looks at tables the same connection queried, so `sqlite_stat1` would stay absent.
Resolution: `ANALYZE` runs once with the conversion; the daily job runs `PRAGMA optimize=0x10002` (SQLite 3.53.3 is bundled).

### 11. Summed `Measure` never matches the walk: Major, FIX
Reviewers: neutral, minimalist. Where: Lifecycle 5
Finding: `RawSnapshots.Bytes` counts deleted rows, the walk counts sidecars, avatars, diagnostics and orphan folders, and existing media rows have no `Bytes`. The weekly mismatch notice would fire every week. Nobody reported the daily walk as slow.
Resolution: Item 5 and step 4 are removed. The walk stays.

### 12. Backup rotation had config keys nobody asked for: Nit, FIX
Reviewers: minimalist. Where: Lifecycle 1
Finding: `retention.backups_daily` and `retention.backups_weekly` are flexibility with no requester.
Resolution: Constants in `Maintenance`: 7 daily and 4 Sunday copies. The Sunday tier stays because a corruption noticed after a week still has a good copy.

### 13. `integrity_check` of the copy and the backup block the scheduler tick: Nit, DECLINE
Reviewers: performance. Where: Lifecycle 1; `Scheduler.cs` L100-104
Finding: Both run inline in `Tick` on the slow mount, once a day. The reviewer proposes `quick_check`.
Resolution: Declined. The owner asked for an integrity check, and a once-a-day pause of the dispatcher costs nothing the owner sees. The duration goes into the maintenance log line.

### 14. Retention has no driver: Major, CONTEST
Reviewers: minimalist. Where: Lifecycle 4
Finding: The tables hold at most 35k rows and nothing is slow.
Resolution: Contested for the raw tables, accepted for the rest after the step-back verdict. `RawSnapshots` grows by about 800 rows a day and nothing ever removes a row; the one rule left bounds that. Runs and visits are small; the Manage overview read of all runs becomes per-platform queries, and the coverage read stays.

### 15. Wrong facts and small gaps: Nit, FIX
Reviewers: neutral, ux. Where: throughout
Finding: `Initialize` is at `FeedDb.cs` L45, the pragmas at L50, the daily job at `Scheduler.cs` L100-104; the refilter write is in `VisibilityStore.cs` L26; `docs/04-instance.md` L150 (`render_cap`: "no paging") contradicts paging.
Resolution: Anchors corrected. The `render_cap` row joins the spec updates.
