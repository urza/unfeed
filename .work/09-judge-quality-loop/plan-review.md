# Round 1 (2026-11-10)

Target: `docs/plans/09-judge-quality-loop.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: a table that grows per judgment, a backfill of about 7,900 rows, queries on every Manage render), ux (reason: a CLI command, a Manage tab, hint text). Security was left out: no new untrusted input beyond one owner query parameter, no new dependency, shell command or network call.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: CHANGE COURSE

Why: The trial is the real gain. Today the only test of a policy edit is a `rescore --all` that overwrites about 7,900 verdicts, and the owner edits policy often. The Judge tab, `JudgeQuery` and the `Kv` summary serve 1 disagreeing thumb today. The hidden feed with `owner=llm` already shows each `llm` hide with its score and reason in the card trail.

Instead: Ship `rescore --trial` without the `Kv` summary, because the CLI output is the report. Also ship the `owner=llm` filter, the `curate` grouping and the chip titles. In `curate`, mark the uncurated thumbs that disagree with the current verdict. Defer the Judge tab and `JudgeQuery` until thumbs and owner unhides reach a count that a list helps with. This removes a projection, a page, a browser test and a spec section.

User (owner proxy, 2026-11-10): accepted with one adjustment. The `curate` report also lists the owner unhides that disagree with the current verdict (one predicate, a few printed lines, no page), because the owner named "where the judge disagrees with my thumbs and with my unhides" as part of the minimum outcome. The Judge tab, `JudgeQuery` and the stored summary are out of scope (decision 8 in the plan).

## Skeptic's cons

1. **Trial results will mix three different causes.** (Target design 3; the table row 1,739 of 7,880.) Most sampled posts carry a verdict from an older version, so a diff mixes the newest edit, earlier edits and model noise, and the plan has no baseline run. Price: the owner trusts "would unhide 14" and runs `rescore --all` for a change that was noise.
   Answer: Agree in part. The trial cannot separate the causes, and the plan now says so: each line names whether the compared verdict was made under the current version or an older one, and the summary counts the current-version comparisons as the noise floor. A baseline is one `rescore --trial` before the edit; the plan does not add a second mechanism for it.
   User (owner proxy, 2026-11-10): accepted as reworded (Target design 1).

2. **The disagreement query works on data that does not exist.** (Problem and evidence; items 4 and 5.) 9 thumbs in six weeks, none on an `llm` hide; the Judge tab's main list will be almost empty. Price: a projection, a tab and a browser test for one or two rows.
   Answer: Agree on the count: the live copy shows 1 disagreeing thumb today. Disagree on the conclusion: the owner named "see where the judge disagrees with my thumbs and my unhides" as the minimum outcome, and the plan now adds owner unhides (plan 02's `OwnerUnhiddenRevision`) as the second signal, plus the 215 `llm` hides of the last 7 days, which is the list the owner has no way to see today. The tab shows empty states.
   User (owner proxy, 2026-11-10): first "keep the query and the tab with empty texts"; after the step-back verdict the tab and the projection are deferred. The `curate` report marks the disagreeing thumbs and lists the disagreeing unhides; the hidden feed with `owner=llm` lists the hides.

3. **The scope is too large for the real need.** (Whole plan.) Item 3 alone shows what an edit changes; two tables, a backfill, retention, a UI tab, a CLI rewrite and six spec files come with it. Price: a long branch and more migration risk.
   Answer: Agree: the two tables, the backfill, the retention job and the config key are gone (decisions 1 to 3). After the step-back verdict the tab and the projection are gone too. What stays is the trial, one filter, a `curate` fix that the spec already asks for plus the disagreement marks, and two title strings. No migration.
   User (owner proxy, 2026-11-10): accepted; the plan is reduced.

4. **The retention rule breaks the stated outcome.** (Outcome; sequence step 7.) "Every verdict ever made stays available" against "keep 20 per post, trials for 30 days". Price: the next developer does not know which rule applies.
   Answer: Agree: both the sentence and the retention rule are gone with the table.
   User (owner proxy, 2026-11-10): accepted.

5. **The size estimate counts the wrong load.** (Open question 1.) Each `rescore --all` adds about 7,800 rows, more than daily judging. Price: a cap set from wrong numbers.
   Answer: Agree; moot now that the table is gone.
   User (owner proxy, 2026-11-10): accepted.

6. **The policy ledger is mostly empty and copies owner data.** (Item 2; step 1.) `PolicyText` is null for the old versions and the backups already hold every text. Price: a second copy of private text and a ledger with gaps.
   Answer: Agree: the table is gone (decision 2). The per-version counts come from `GROUP BY PrefsVersion` on `Posts`.
   User (owner proxy, 2026-11-10): accepted.

7. **The "loop" stays open.** (Out of scope.) The owner sees a disagreement, but the only action is still a manual `curate --write`. Price: the thumb hint stays inaccurate and item 7 leaves the choice to the reviewer.
   Answer: Agree in part. The plan does not automate the loop, on purpose: an automatic policy edit from 9 thumbs would be guesswork, and the owner's brief asks for visibility first. The hint text is decided now (decision 6): it says the vote is remembered for this author. The `curate` report is where the owner sees the disagreements and writes the policy line.
   User (owner proxy, 2026-11-10): accepted; no automatic policy edits.

## Findings

### 1. The non-trial verdict history has no reader: Major, ESCALATE
Reviewers: minimalist, skeptic (cons 3 to 5). Where: Target design 1; sequence steps 1, 2, 7; open question 1
Finding: No feature reads old non-trial `Verdicts` rows. Trials compare against the post columns, `JudgeQuery` reads the post columns and the `Feedback` snapshot, and the version counts can come from `Posts`. The table costs a backfill, an insert per judgment, a retention job and a config key.
Resolution: Question for the owner: is verdict history a real need? Recommended: no. The orchestrator's brief allows a course without the append-only table. Trials write one `Kv` summary (`judge:last-trial`); the CLI output is the full report.
User (owner proxy, 2026-11-10): no `Verdicts` table, no previous-verdict columns either; decisions 1 and 3 in the plan. After the step-back verdict the `Kv` summary is gone too: the CLI output is the report. The feature can add a history later if the owner asks for one, and nothing in this course blocks that.

### 2. The `PolicyVersions` table copies data that already exists: Major, ESCALATE
Reviewers: minimalist, skeptic (con 6). Where: Target design 2; open question 2; `Prompts.Version` (`Prompts.cs` L72); `ManagementFiles.cs` L44
Finding: `PrefsVersion` already holds the four hashes; `FirstSeenAt` equals `MIN(JudgedAt)`; counts per version equal `GROUP BY PrefsVersion`; `PolicyText` copies `data/settings-backups/` and would be null for 11 of 12 versions.
Resolution: Question for the owner: keep a ledger table? Recommended: no. A `GROUP BY PrefsVersion` can list the versions when a page needs it.
User (owner proxy, 2026-11-10): no table; decision 2. With the Judge tab deferred, no version list is built.

### 3. A trial through `Processing.Run` changes the post row: Major, FIX
Reviewers: neutral. Where: Target design 3; sequence step 4; `Processing.cs` L121, L171-175, L247
Finding: `Stamp` writes `LlmAttemptedAt` and clears `LlmTokenLimit` on every dispatch; the rescore selection takes only unjudged or stale posts, so "N recent judged posts" under the current version selects nothing; `Apply` writes the post. The plan did not say how a trial avoids the three paths.
Resolution: Target design 1 now specifies `WorkScope.Trial`: a judged-for-current-content selection without the stale condition, a read-only eligibility check instead of `Stamp`, and a compare-and-print apply that writes nothing to the post. The test compares all post columns before and after.

### 4. Trial hide state must use the full model rule: Major, FIX
Reviewers: neutral, ux. Where: Target design 3; `Identity.cs` L103
Finding: A plain threshold compare reports "would hide" for a post the owner unhid or for a shielded author; `Visibility.Model` checks `OwnerUnhiddenRevision` and the `llm` shield.
Resolution: The trial calls `Visibility.Model` on a copy of the post that carries the trial score; the test covers the owner-unhide case.

### 5. Item 6 changes the wrong command: Major, FIX
Reviewers: neutral, ux. Where: "Current behavior" bullet 5; Target design 6; spec 9.6 L157; 13-cli L35-36; `Program.cs` L89
Finding: The spec puts the grouped report on `curate` without flags; `feedback` is a flat list by spec. The code bug is in `curate`, which prints the same flat list. As written, the plan would change `feedback` and leave `curate` wrong.
Resolution: Target design 5 retargets the grouping to `curate`. `feedback` stays flat.

### 6. Trial diff measures more than the last edit: Major, FIX
Reviewers: ux, skeptic (con 1). Where: Target design 3
Finding: 1,822 of 7,897 judged posts are under the newest version; a sample mixes earlier edits and model noise with the new edit.
Resolution: Each printed line names whether the compared verdict was made under the current version; the summary counts current-version comparisons as the noise floor.

### 7. The Judge tab has no empty states and no next step: Major, FIX (moot)
Reviewers: ux. Where: Target design 5; sequence step 5
Finding: With 1 disagreeing thumb and no trial, the lists are mostly empty; the plan gave no text and did not say how to start a trial.
Resolution: First the plan gave the empty texts and the command hints. After the step-back verdict the tab is deferred, so the finding is moot; the `curate` report prints "no uncurated signals" and "no unhide disagrees with the current verdict" when the lists are empty.

### 8. Post deletion does not delete verdicts: Major, FIX (moot)
Reviewers: neutral. Where: sequence step 1; `StorageCleanup.cs` L47-50, L79-82
Finding: `clean-hidden` and reset delete posts with explicit `ExecuteDelete` calls; a `Verdicts` table would keep orphans or fail on a foreign key.
Resolution: Moot: no table and no stored summary.

### 9. Disagreements built for two surfaces: Nit, FIX
Reviewers: minimalist. Where: Target design 4 to 6
Finding: A per-row disagreement flag in the CLI and the same list on the Judge tab, for 9 rows.
Resolution: First the CLI got no flag and the Judge tab was the one surface. After the step-back verdict the `curate` report is the one surface: it marks the disagreeing thumbs and lists the disagreeing unhides (decision 5).

### 10. A new `--sample` option when `--limit` exists: Nit, FIX
Reviewers: minimalist. Where: Target design 3; `Program.cs` L28
Finding: `--sample N` duplicates `--limit N`.
Resolution: `rescore --trial [--limit N]` with the existing selection flags (decision 4). Stratified sampling (open question 3) is dropped; `--since` focuses a sample.

### 11. A config key for the retention cap: Nit, FIX (moot)
Reviewers: minimalist. Where: sequence step 7
Finding: Nobody asked to tune the cap.
Resolution: Moot: no retention job.

### 12. An open choice is left in the design (thumb hint): Nit, FIX
Reviewers: minimalist, ux, skeptic (con 7). Where: Target design 7; open question 4
Finding: "If the reviewer prefers, keep the current text" left two paths. The ux reviewer adds that "can be turned into a policy line" names a CLI path the web owner cannot find.
Resolution: Decision 6: the chip titles say "remembered for this author". The `curate` report is the path to a policy line, as spec 9.6 says.

### 13. The feed link uses the wrong query parameter: Nit, FIX
Reviewers: ux. Where: Target design 5; "Current behavior" bullet 3; `Feed.Web/Program.cs` L35
Finding: The main feed reads `?hidden=1`; `scope=hidden` exists only on person pages.
Resolution: The link is `/?hidden=1&owner=llm`; the bullet is corrected.

### 14. Line references are stale: Nit, FIX
Reviewers: neutral. Where: Problem and evidence
Finding: `Prompts.Memory` is at L104-108; the `curate` code at L77-93; the verdict apply at L247.
Resolution: Corrected. The evidence table is remeasured on a copy of the live database (2026-11-10).

### 15. Performance: All good
Reviewers: performance. Load assumed: one owner, SQLite, about 7,900 judged posts, about 300 judgments a day, `llm.parallel` 1. Checked: table growth under the cap (about 160k rows worst case), the insert inside the existing `SaveChangesAsync`, the Judge tab queries (a join over 9 rows, one `GROUP BY` over about 8k posts, bounded lists), the one-time backfill (about one second), and the trial lock time (model time, bounded by `--limit`, shorter than `rescore --all`). After decisions 1 and 2 the growth and the backfill no longer exist.
