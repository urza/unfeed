# Round 1 (2026-11-10)

Target: `git diff af937da...HEAD` (branch `08-storage-lifecycle`), spec `docs/plans/08-storage-lifecycle.md`

Reviewers: neutral, minimalist, performance (reason: a VACUUM and a daily copy of a 400 MB file on a slow mount, new queries on the Manage overview and the feed read), ux (reason: a new CLI command, the backup line in `status` and Manage, the runbook). Security was left out: the only new file paths are built from the instance folder and migration names, and the app has one owner.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The database is the only copy of verdicts and feedback after 60 days. It had no backups, and 67% of the file was free pages, so a daily verified copy and a one-time VACUUM fix real, measured problems. About 100 lines of plain SQLite features (VACUUM INTO, incremental auto-vacuum) is a fair price, and the live-copy drill (412 MB down to 108 MB, integrity ok) shows it works.

The step-back agent weighed the rest: the bounded reads target small tables and buy little, and the `raw_rows_retention_days` key is extra surface for one rule, but both are cheap and done, so a revert costs more than it saves. The daily incremental vacuum is one pragma call. The remaining risk is that the backups sit on the same disk as the live file; the runbook's off-host copy is the step that covers a disk loss.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. A parallel start could crash the second process on the rename: Major, FIX
Reviewers: neutral. Where: `Maintenance.cs` (`Snapshot`), plan target design 2
Finding: Two processes with a pending migration both pass the `File.Exists(target)` check during the same `VACUUM INTO`. The loser of the rename threw `IOException` from `File.Move` and never migrated, against the plan's "the second process skips the copy".
Resolution: The move is `try { File.Move } catch (IOException) when (File.Exists(target)) { }` with a comment at the line. The `finally` block removes the loser's temporary copy.

### 2. A failed pre-migration copy stopped every start with a raw SQLite error: Major (ux), Nit (neutral), FIX
Reviewers: ux, neutral. Where: `FeedDb.cs` (`Initialize`), `docs/RUNBOOK.md`
Finding: With a full disk or an unwritable `backups/` folder the host and every CLI command refused to start with `SQLite Error 13` and no mention of the copy. Nothing said that the refusal is deliberate.
Resolution: `Initialize` rethrows as `IOException("pre-migration backup to <path> failed: <cause>; free disk space or remove old copies in backups/, then start again")`, with a comment that a start without a restore point is refused on purpose. The RUNBOOK has the line.

### 3. The failure line hid the age of the last good copy: Major, FIX
Reviewers: ux. Where: `Maintenance.BackupLine`
Finding: After five failed days `status` showed only `backup: FAILED ...`, although `backup:last` was still in Kv.
Resolution: The line reads `backup: FAILED <error>; last good <time or never>; N copies in backups/`. The test asserts `last good never`.

### 4. `raw_rows_retention_days` as config instead of a constant: Major, ESCALATE
Reviewers: minimalist. Where: `Configuration.cs`, `Manage.razor`, `Management.cs`, docs 04, 05, 07
Finding: The rotation numbers are constants "because nobody asked for other numbers"; the same reason applies to this key. The key costs a property, a validation entry, a Manage field, a save key and three doc rows.
Resolution: Question for the owner. Recommended: keep the key. It sits in the retention panel next to `raw_retention_days` and the media keys, with the same `0 means never` switch, so the owner has one place and one rule shape for every retention; a constant would be the one retention the owner cannot turn off.
User (owner proxy, 2026-11-10): keep the key, for the consistency of the retention panel.

### 5. `trim` could live inside `raw prune`: Nit, CONTEST
Reviewers: minimalist. Where: `Program.cs`, `Scheduler.cs`, `Maintenance.Trim`
Finding: A new verb, a new scheduler step and a second processing-lock method for three lines of deletion.
Resolution: Contested. `Prune(raw: true)` skips a platform when `raw_retention_days` is 0 (`Maintenance.cs` L129) and replaces its cutoff with `--before`; both would then apply to row retention, which has its own key and no date override. `Prune` also takes each ingest lock and skips a busy platform, which the row rule does not need. The spec (13.2) lists `trim`.

### 6. The explicit diagnostics delete repeats the cascade: Nit, DECLINE
Reviewers: minimalist. Where: `Maintenance.Trim`
Finding: The model cascades `RawDiagnostics` from `RawSnapshots`, and the EF SQLite provider enables `foreign_keys`.
Resolution: Declined. The summary line needs the count anyway, and the deletion must not depend on a connection pragma; a comment at the line now says so.

### 7. No test drives the `integrity_check` rejection branch: Nit, DECLINE
Reviewers: neutral. Where: `LifecycleTests.cs`, plan step 1
Finding: The failure test makes `VACUUM INTO` fail before the check runs.
Resolution: Declined. `VACUUM INTO` cannot be made to write a corrupt copy on purpose; the plan's "Notes from implement" records the gap.

### 8. A killed process leaves its temporary copy for good: Nit, FIX
Reviewers: performance. Where: `Maintenance.Rotate`
Finding: `Rotate` matched only `feed-????????.db` and `pre-*.db`, so a `.tmp` left by an OOM kill during `VACUUM INTO` stayed and grew `disk:summary` with no cause.
Resolution: `Rotate` deletes `*.tmp` files older than one hour; the age guard keeps a copy that a parallel process writes right now. The rotation test has a stale and a fresh `.tmp`.

### 9. Manage text said the file does not shrink; "none yet" gave no reason; two `trim` messages misled: Nit, FIX
Reviewers: ux. Where: `Manage.razor`, `Maintenance.BackupLine`, `Maintenance.Trim`
Finding: "its file may not shrink" is false after compaction; `backup: none yet` did not say that backups need the scheduler; `trim` with the key at 0 reported "processing busy" first, and the summary said "deleted more than N days ago" for a capture-date rule.
Resolution: The panel says freed space goes back to the disk at the next daily compaction. The line reads `backup: none yet (daily backups run only while the scheduler is enabled)`. `Trim` checks the key before the lock, and the summary says "rows captured more than N days ago".

### 10. Performance: All good
Reviewers: performance. Checked: one index probe per platform in the feed read (cheaper than the DISTINCT); fixed counts of small run queries on the overview; `BackupLine` lists at most about 13 files; `Trim` scans once a day with the diagnostics index; `Backup` and `Compact` run in the daily job, not on a request; `Initialize` adds two reads of the history table; `backups/` is bounded by rotation and shown in `disk:summary`.
