# Round 1 (2026-11-10)

Target: `git diff 8fc76ee...HEAD` (branch `09-judge-quality-loop`), spec `docs/plans/09-judge-quality-loop.md`

Reviewers: neutral, minimalist, ux (reason: a CLI command and its output, a feed banner, chip titles, four doc changes). Security was left out: the one new input is a query parameter checked against `ClosedValues.HideOwners`; no new dependency, shell command or network call. Performance was left out: the trial is bounded by `--limit` and runs the existing judge path; the report queries read 9 feedback rows and a bounded post set.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: Today the only way to test a policy edit is `rescore --all`, which overwrites about 7,900 verdicts. The owner made 12 policy versions, so a sample trial that writes nothing meets a real need. The trial costs about 40 guarded lines in `Processing.Run`, and tests and a run on a live copy cover it. The `owner=` filter, the `curate` fix and the chip titles are small. They also fix a spec mismatch and a false UI promise.

The step-back agent adds: the cheapest option is a script that runs `rescore` on a copy of `feed.db` and diffs the verdict columns, but each use takes manual SQL and `rescore` reaches older-version posts only through `--all`. The `Trial` branches in `Stamp`, `Apply` and the backoff sit in the core write path and are the real risk; the review checked that a failed trial writes nothing. The disagreement marks give little value today (9 thumbs, 1 disagreement) but cost one predicate.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. The page did not show that the owner filter is on, and navigation dropped it: Major, FIX
Reviewers: ux. Where: `FeedView.razor` L10, L24, L51, L76; `FeedQuery.cs` L10
Finding: `FeedPage` had no owner field, so `/?hidden=1&owner=llm` showed the unfiltered banner text, the Hidden button linked to the unfiltered list, and `owner=LLM` fell back to all hidden posts with no sign.
Resolution: `FeedPage.Owner` is set by `Read`; the banner adds ", only those hidden by llm" and an "All hidden posts" link; `Link` takes an optional owner that the two Hidden buttons pass; the route lowercases the value before the check. The view and platform pills were not changed: they already leave the hidden scope. Chapter 15 names the banner text. The filter test asserts `Owner`.

### 2. `UnhideDisagrees` was a test-only copy of the rule `Curate` uses: Major, FIX
Reviewers: minimalist, neutral. Where: `JudgeReport.cs` L20, L44; `JudgeTrialTests.cs` L91-92
Finding: No production code called the predicate; `Curate` runs the same rule as an EF query, so an edit to one would leave the other unchanged while the unit test still passed.
Resolution: The predicate and its test lines are gone; the EF query carries the comment and the report test covers the unhide block from header to row. The plan's target design and implement note say so.

### 3. The unhide block showed a capped count as the total: Nit, FIX
Reviewers: neutral, ux. Where: `JudgeReport.cs` L44
Finding: `Take(50)` with a header that printed `unhides.Count` would say "50 owner unhides" for 60.
Resolution: No cap; the plan asked for none, and the live copy holds 0 such rows today.

### 4. One shared `Take(200 * N)` could starve an author's memory block: Nit, FIX
Reviewers: minimalist, neutral. Where: `JudgeReport.cs` L30
Finding: `Processing` takes the newest 200 rows per author; the report took one limit for all authors together, so a busy author could leave another with no memory lines.
Resolution: The cap is gone. `Prompts.Memory` keeps the newest 40 rows of 180 days per author, so the result equals what the judge receives.

### 5. The last CLI line of a trial read like a real rescore: Nit, FIX
Reviewers: ux. Where: `Program.cs`, the counts line after the trial summary
Finding: `rescore all: selected=100, completed=100, ...` followed "nothing was written".
Resolution: The line reads `rescore --trial all: ...` for a trial.

### 6. The chapter 13 row is said to lack its closing pipe and the exit text: Nit, CONTEST
Reviewers: neutral. Where: `docs/13-cli.md` L31
Finding: The reviewer read the row as cut.
Resolution: The row ends with `| 1 when any call failed; 2 model off, unknown platform or author, or `--trial` with `--all` |` and renders as one row in `spec.html` (table cell at the `rescore` entry). Nothing changed.

### 7. Verification on a copy of the live instance: recorded
A copy of `data/feed.db` (pre-plan-02 schema) in the scratchpad, with the model endpoint pointed at a closed port: `rescore --trial --limit 6` selected 6 judged posts, failed all 6 calls, printed the summary with `nothing was written` and left every verdict and visibility column of all 10,856 posts identical to the pre-migration backup; no backoff key was added (4 before, 4 after). The only columns that differed were `MediaManifestJson` and `ContentHash` on 9,702 posts, written by the one-time media-key rekey of plan 03 at startup, which this copy had not had yet. `curate` on the copy printed 6 author groups, 9 thumbs, 6 memory lines, 1 disagreement mark and the empty unhide line. A trial with a live model was not possible from the sandbox.
