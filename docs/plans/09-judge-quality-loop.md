# Plan 09: Build the judge-quality loop

Status: proposed 2026-11-09 from the step-back review. Reviewed and reduced on 2026-11-10 (plan review: CHANGE COURSE, smaller course taken with one adjustment; code review: GO). Reports in `.work/09-judge-quality-loop/`. Implemented on 2026-11-10 on branch `09-judge-quality-loop`; see "Notes from implement".

## Outcome

The owner can try the current policy on a sample of judged posts before a `rescore --all`; a trial writes no verdict and the live verdicts stay as they are. The owner can list the `llm` hides in the hidden feed, and the `curate` report marks the thumbs and the owner unhides that disagree with the current verdict.

## Problem and evidence

The card footer says the agent learns from a thumb (`docs/15-ui.md` L117). In practice a thumb becomes one sentence per author in the prompt (`Prompts.Memory`, `src/Feed.Core/Application/Prompts.cs` L104-108). Learned policy lines need the manual `curate --write` command (`src/Feed.Cli/Program.cs` L77-93). Verdicts are overwritten in place on the post row (`Processing.cs` L247). Nothing shows the owner where a verdict and a thumb disagree, and the only way to test a policy edit is `rescore --all`, which re-judges about 7,900 posts and overwrites every answer.

Live instance, 2026-11-10 (copy of `data/feed.db`):

| Measure | Value |
|---|---|
| Posts; judged for their current content | 10,856; 7,897 |
| Feedback rows in six weeks | 9 (8 up, 1 down), none curated |
| Thumbs that disagree with the current verdict | 1 (a thumb down on a visible post) |
| Posts hidden by `llm`; in the last 7 days | 2,216; 215 |
| Distinct `PrefsVersion` values; posts under the newest | 12; 1,822 |
| Views or lists that filter by `HiddenBy = llm` | none |

The owner edits policy often (12 versions, three of them with more than 900 posts). The live database is not yet migrated to plan 02, so the count of owner unhides against an `llm` hide is unknown.

## Current behavior to keep in mind

- `Feedback` entity (`Entities.cs` L82-86) already snapshots `CategoriesAtVote`, `ScoreAtVote`, `ReasonAtVote`, `Scope` and `ViewKey`.
- `FeedQuery.cs` L119-126 builds the card trail: shields, score, categories, reason, "older policy" flag, hidden by. L125 compares `PrefsVersion` to the current version.
- The main feed reads `?hidden=1` and `?unsorted=1` (`Feed.Web/Program.cs` L35); `?scope=hidden` exists only on person pages (`PersonRoutes.cs`). No owner filter.
- `rescore` accepts `--all`, `--platform`, `--limit`, `--since`, `--post-id`, `--post-ids`, `--author`, `--text-only` and runs `Processing.Run` directly under a Run of kind `rescore`. Selection (`Processing.cs` L121) takes unjudged and stale posts; posts judged under an older version only with `--all` or `--author`. `Stamp` (L171-175) writes `LlmAttemptedAt` and clears `LlmTokenLimit` on every dispatch; `Apply` (L240-255) writes the verdict columns and calls `Visibility.Model`.
- `Visibility.Model` (`Identity.cs` L103) is the only rule for an `llm` hide: score below the threshold, no owner unhide of this content (`OwnerUnhiddenRevision`), no `llm` shield.
- The `feedback` command prints a flat list, as spec 13-cli L35 says. `curate` without flags prints the same flat list (`Program.cs` L89), but spec 9.6 and 13-cli L36 describe a report grouped by author with the memory sentences. The bug is in `curate`.
- `storage clean-hidden` and reset delete posts, feedback, likes and media with explicit `ExecuteDelete` calls (`StorageCleanup.cs` L47-50, L79-82).
- Thumbs never change visibility (binding rule 11). This plan keeps that.

## Target design

1. **Trial judging.** `rescore --trial [--limit N] [--since D] [--platform P] [--author REF]`, default limit 100. `WorkScope` gets a `Trial` flag. Under the flag, `Processing.Run`:
   - selects judged posts for their current content (`CategoriesJson != null && VerdictContentRevision == ContentRevision`), visible or hidden by `llm`, newest post first, with the same `--since`, `--platform` and `--author` filters as today; the "stale version" condition does not apply;
   - checks eligibility before dispatch with a read (same predicate as `Stamp`) and writes nothing to the post;
   - on a verdict, computes the trial hide state with `Visibility.Model` on a copy of the post that carries the trial score and reason, and compares it with the current state: hidden by `llm` or not, and the restricted category set;
   - writes no post column and no verdict; the processing lock and `llm.parallel` apply as for any rescore;
   - prints one line per post whose hide state or category set differs: post id, platform, old score and new score, `would hide` / `would unhide` / `labels changed`, and whether the old verdict was made under the current version or an older one;
   - prints a summary line: sampled, would hide, would unhide, labels changed, unchanged, failed, and how many compared verdicts were under the current version (differences there are model noise, not the edit).
   The CLI output is the whole report. Nothing is stored.
2. **`owner=` filter on the hidden scope.** `FeedQuery.Read` takes an optional `owner` for the hidden scope and filters `HiddenBy`. The route reads `?owner=`; `/?hidden=1&owner=llm` lists the `llm` hides, each card with its score and reason in the trail as today. Unknown values fall back to no filter.
3. **Make `curate` match the spec, and mark disagreements.** `curate` without flags groups the uncurated signals by author and prints the memory sentences `Prompts.Memory` would produce for that author. A signal that disagrees with the current verdict gets a mark: a thumb down on a visible post with the current score at or above the threshold; a thumb up on a post hidden by `llm`, or on a post whose current score is below the threshold. After the groups, one block lists the owner unhides that disagree: posts with `OwnerUnhiddenRevision == ContentRevision`, judged for the current content, score below the threshold (post id, platform, score, reason). The thumb predicate and the report live in one small static class `JudgeReport` in `Feed.Core/Queries`, so a unit test covers them without the console. `feedback` stays a flat list.
4. **Keep the UI promise honest.** The thumb chip titles become "More like this (remembered for this author)" and "Less like this (remembered for this author)".

## Contracts to preserve

- Binding rule 11: a thumb writes a row and nothing else. Trial verdicts and disagreement lists never change visibility.
- Binding rule 6: trial judging runs under the processing lock and counts against `llm.parallel`.
- Binding rule 12 (fail-open): a trial compares only posts judged for their current content.
- Rescore remains explicit. No automatic rescore follows a trial. A trial writes nothing.
- No new table, no migration, no new config key, no new page.

## Implementation sequence

1. `WorkScope.Trial`; selection, eligibility read, compare-and-print in `Processing.Run`. CLI flag and usage line. Test with the fake HTTP model: the post row is byte-identical after a trial (compare all columns), the printed lines and the summary match the fake verdicts, a trial on a post hidden by `llm` whose trial score passes the threshold reports `would unhide`, and a trial on a post the owner unhid reports no `would hide`.
2. `JudgeReport` predicates with a unit test on synthetic rows: thumb down on a visible high-score post; thumb up on an `llm` hide; thumb up on a low-score visible post; an owner unhide of a low-score post; and a thumb that agrees (not marked).
3. `owner=` filter in `FeedQuery` and the route. Test: `hidden=1&owner=llm` lists only `llm` hides.
4. `curate` grouping and marks. Test the output shape against the spec text (grouped by author, one memory block per author, the disagreement mark, the unhide block).
5. Thumb chip titles.

## Spec updates

- `docs/09-llm.md`: new 9.11 "Trial judging"; 9.6 gains the disagreement marks and the unhide block in the `curate` report.
- `docs/13-cli.md`: `rescore --trial`; the `curate` row names the marks.
- `docs/15-ui.md`: `owner` filter on the hidden feed, thumb chip titles.
- `docs/02-overview.md` 2.3: no rule change; rule 11 holds.

## Verification

- Unit and browser tests listed above. `ProcessingTests` and `ModelSafetyTests` pass unchanged.
- Private instance (on a copy): a trial of 100 posts completes under the normal pool, the hidden count and every post row are unchanged.

## Decisions taken at review (2026-11-10)

1. No `Verdicts` table. Nothing in the plan read the history of non-trial verdicts: trials compare with the post columns, disagreements read the post columns and the `Feedback` snapshot. A 7,900-row backfill, an insert per judgment, a retention job and a config key would serve one sentence in the outcome. The owner's minimum (see disagreements, try a rescore on a sample without overwriting) needs no history. The previous verdict is not kept either; a trial compares against the live verdict, which is the one the owner sees.
2. No `PolicyVersions` table. `PrefsVersion` already carries the four hashes, the per-version counts are a `GROUP BY` on `Posts`, and `data/settings-backups/` already holds every saved preferences text. A ledger would hold `PolicyText = null` for 11 of 12 versions.
3. Trial results are not stored. The CLI output is the full report.
4. `--trial` reuses `--limit`, `--since`, `--platform` and `--author`; no `--sample` flag and no stratified sampling. The owner can pass `--since` to focus a sample.
5. The grouped report goes on `curate` without flags, where the spec puts it. `feedback` stays flat; the `curate` report is the one disagreement surface.
6. The thumb chip titles change to say what happens. The old text promised learning that nothing performs.
7. Owner unhides count as disagreements. They are the owner's strongest signal against a verdict and cost one predicate.
8. Step back (CHANGE COURSE) asked to ship the trial without a stored summary, the `owner=llm` filter, the `curate` grouping with disagreement marks and the chip titles, and to defer the Judge tab and `JudgeQuery` until the thumbs reach a count that a page helps with. The owner proxy took that course with one adjustment: the `curate` report also lists the owner unhides that disagree, because the owner named disagreement with unhides as part of the minimum outcome; it is one predicate and a few printed lines, no page.

## Out of scope

Prompt redesign, structured output decoding, summary merging, automatic policy edits from thumbs, any change to how thumbs reach the prompt, verdict history, a policy ledger, a stored trial summary, a Judge tab on Manage and a `JudgeQuery` projection (deferred until the thumbs reach a count that a page helps with), a trial button on Manage (the trial is a model run and belongs to the CLI like `rescore`).

## Notes from implement

- The trial lives inside `Processing.Run` as a `WorkScope.Trial` flag: the selection takes judged posts for their current content, `Stamp` becomes a read-only eligibility check, a failed result writes no `LlmError`, a dead batch sets no backoff marker, and a verdict goes to `Compare` instead of the apply block. `WorkCounts.Trial` carries the five trial counts; the CLI prints the summary line from them.
- `Compare` loads the post untracked and sets the trial score on that copy, so `Visibility.Model` runs the full rule (owner unhide, `llm` shield) and nothing reaches the database.
- The curate report is `JudgeReport.Curate` in `Feed.Core/Queries`, next to the thumb predicate `Disagrees`, so the unit test checks the lines without the console. The unhide rule is one EF query inside `Curate` (a C# predicate cannot run in SQL), covered by the report test. The memory sentences shown per author are those a new post by the author would carry (no post excluded, curated rows included), which is what `Processing` passes to `Prompts.Memory`.
- The owner filter is validated against `ClosedValues.HideOwners` in the route; the counts of the hidden scope stay unfiltered, as the plan says.
- `--trial` refuses `--all` (the stale condition has no meaning in a trial) and takes a default limit of 100 only when `--limit` is absent.
- No browser test was added: the plan has no page any more, and the chip title and the empty message are plain strings covered by the fast tests.
