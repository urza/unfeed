# Plan 09: Build the judge-quality loop

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

The owner can see what the judge hides and why, can compare a policy edit against recent posts before rescoring history, and can see where thumbs disagree with verdicts. Every verdict ever made stays available for that comparison.

## Problem and evidence

The card footer says the agent learns from a thumb (`docs/15-ui.md` L116). In practice a thumb becomes one sentence per author in the prompt (`Prompts.Memory`, `src/Feed.Core/Application/Prompts.cs` L78-82). Learned policy lines need the manual `curate --write` command (`src/Feed.Cli/Program.cs` L87-104). Verdicts are overwritten in place on the post row (`Processing.cs` L243). No table keeps a verdict history or a policy-version history.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| Feedback rows in six weeks | 9 (8 up, 1 down), none curated |
| Posts hidden by `llm` | 2,195, none with a thumb |
| Distinct PrefsVersion values | 12 |
| Posts judged under the newest version | 1,739 of 7,880 |
| Views or lists that filter by `HiddenBy = llm` | none |

The owner edits policy often (12 versions) and has no way to see what each edit changed. The only tool is `rescore --all`, which re-judges about 7,800 posts and destroys the previous answer.

## Current behavior to keep in mind

- `Feedback` entity (`Entities.cs` L81-85) already snapshots `CategoriesAtVote`, `ScoreAtVote`, `ReasonAtVote`, `Scope` and `ViewKey`.
- `FeedQuery.cs` L119-125 builds the card trail: shields, score, categories, reason, "older policy" flag, hidden by. L123 compares `PrefsVersion` to the current version.
- `?scope=hidden` and `?scope=unsorted` exist (`FeedQuery.cs` L110). No owner filter.
- `rescore` accepts `--all`, `--platform`, `--limit`, `--since`, `--post-id`, `--post-ids`, `--author`, `--text-only` and runs `Processing.Run` directly under a Run of kind `rescore`.
- The `feedback` command prints a flat list. Spec 9.6 and 13-cli L36 describe a grouped view with the memory text. The code does not match.
- Thumbs never change visibility (binding rule 11). This plan keeps that.

## Target design

1. **Append-only verdict history.** New table `Verdicts`: `Id`, `PostId`, `ContentRevision`, `PrefsVersion`, `InputHash`, `Model`, `Endpoint`, `Score`, `Reason`, `CategoriesJson`, `JudgedAt`, `RunId`, `Trial` (bool). Verdict apply in `Processing.cs` inserts a row and then updates the post columns as today. The post columns stay as the current pointer, so `FeedQuery` does not change. Index on `(PostId, JudgedAt)`.
2. **Policy version ledger.** New table `PolicyVersions`: `Version`, `FirstSeenAt`, `PrefsHash`, `TaxonomyHash`, `Generation`, `ConfigHash`, `PolicyText`, `TaxonomyReplyShape`. `Processing.Run` upserts the current version at start. `PolicyText` is owner data and lives only in `data/`, like the rest of the database.
3. **Trial judging.** New command `rescore --trial --sample N [--since D] [--platform P]`. It selects N recent judged posts, runs the judge under the current version with the normal pool and the processing lock, and writes `Verdicts` rows with `Trial = true`. It does not touch the post row. It prints one line per post whose hide state or category set differs from the current verdict, and a summary line: would hide, would unhide, labels changed, unchanged. The owner then decides on `rescore --all`.
4. **Disagreement query.** New read projection `JudgeQuery`: a thumb down on a visible post with score at or above threshold; a thumb up on a post hidden by `llm`, or on a post whose current verdict scores below threshold. Also: uncurated thumbs, and the count of `llm` hides in the last 7 days with a bounded sample.
5. **Judge tab on Manage.** Shows the disagreement list, the newest `llm` hides with score and reason, the version ledger with counts per version, and the last trial summary. Links go to the existing `?scope=hidden` feed with a new `&owner=llm` filter.
6. **Make `feedback` match the spec.** Group by author, show the memory sentences the prompt would receive, and show the disagreement flag per row.
7. **Keep the UI promise honest.** Change the thumb hint text to say what happens: the vote is remembered for this author and can be turned into a policy line. If the reviewer prefers, keep the current text once the Judge tab exists.

## Contracts to preserve

- Binding rule 11: a thumb writes a row and nothing else. Trial verdicts and disagreement lists never change visibility.
- Binding rule 6: trial judging runs under the processing lock and counts against `llm.parallel`.
- Binding rule 1: `PolicyText` in the database stays in `data/`. Tests use the synthetic instance.
- Rescore remains explicit. No automatic rescore follows a trial.

## Implementation sequence

1. Migration: add `Verdicts` and `PolicyVersions`. Backfill one `Verdicts` row per post that has a verdict today, from the current columns, with `Trial = false`. Backfill `PolicyVersions` from the distinct `PrefsVersion` values with `PolicyText = null` for versions whose text is unknown.
2. Insert into `Verdicts` at verdict apply. Upsert `PolicyVersions` at `Processing.Run` start. Test: two rescores of one post leave two rows and the post shows the latest.
3. `JudgeQuery` with a unit test on the four disagreement cases using synthetic rows.
4. `rescore --trial`. Test with the fake HTTP model: trial rows appear, the post row is byte-identical, the printed summary matches the rows.
5. Manage Judge tab and `&owner=` filter on the hidden scope. Browser test: the tab renders with and without JavaScript, the link lands on the filtered feed.
6. `feedback` command grouping. Test the output shape against the spec text.
7. Retention: the daily job deletes trial verdicts older than 30 days and keeps the last 20 non-trial rows per post. Config key `retention.verdict_history` with that default.

## Spec updates

- `docs/09-llm.md`: new 9.11 "Verdict history and trials"; 9.6 aligned with the grouped `feedback` output.
- `docs/05-data-model.md`: the two new tables and the retention rule.
- `docs/13-cli.md`: `rescore --trial`, `feedback` output.
- `docs/15-ui.md`: Judge tab, `owner` filter, thumb hint text.
- `docs/12-scheduler.md` daily job: verdict retention.
- `docs/04-instance.md`: `retention.verdict_history`.

## Verification

- Unit and browser tests listed above. `ProcessingTests` and `ModelSafetyTests` pass unchanged.
- Private instance: after the backfill, `Verdicts` holds 7,880 rows and the feed renders identically. A trial of 100 posts completes under the normal pool and leaves the hidden count unchanged.

## Open questions for the reviewer

1. Should `Verdicts` keep every row forever? Rows are small (about 300 bytes). At 300 judgments a day that is about 30 MB a year. The plan proposes a cap per post to keep the table bounded.
2. Store `PolicyText` in the database, or only the hashes with a pointer to `data/settings-backups/`? The backups already hold every saved preferences.md version.
3. Trial sample: newest N judged posts, or stratified by hidden and visible? Stratified gives a better view of unhide risk.
4. Should the Judge tab replace the "learns from it" text, or should both exist?

## Out of scope

Prompt redesign, structured output decoding, summary merging, automatic policy edits from thumbs, any change to how thumbs reach the prompt.
