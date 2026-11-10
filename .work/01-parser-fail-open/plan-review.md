# Round 1 (2026-11-10)

Target: `docs/plans/01-parser-fail-open.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: a new repetition query over 36k raws feeds the Manage dot), ux (reason: the plan removes a CLI command and a Manage panel and changes run status texts), security (reason: the parser classifies network responses, a migration drops data, and step 7 commits captures from the private instance)

The owner is away. The session acts as owner proxy; its decisions are marked `User (owner proxy, 2026-11-10)`.

## Step back
Verdict: CHANGE COURSE

Why: `Ingest.File` already stores the posts before it throws on a fatal diagnostic. The raw then replays by itself after a version bump. So an unknown sibling branch loses no data today. It only makes the run `error`, marks the visit `incomplete` and starts browser retries, and all three of those happen in `Capture.cs`. The non-fatal class (the post-identifier probe, impact `unknown`, the wider `reparse --failed`, Compatibility rows for parsed raws) exists only to fix problems it creates itself, such as skeptic cons 1 and 2 and findings 2 and 4.

Instead: Keep the parser classes as they are and skip step 1 and step 4. In `Capture.cs`, the run status follows the browser, `Blocked` is kept apart from `Failed`, and a visit is `captured` when it observed posts. Keep steps 3 and 5 to 7 (health signal, deletion, migration, config).

User (owner proxy, 2026-11-10): accepted. The plan keeps the parser and its version. Steps 1 and 4 (the non-fatal class and its replay path) are removed. The run and the visit no longer fail on a blocked raw, the health signal stays, and the model paths, the schema and the config change as planned. The step-back course fixes the structural problem that the plan names (a fifth of the code copes with a fail-closed parser) with less new mechanism.

## Skeptic's cons

1. **An unknown sibling branch may be the posts.** Where: Target design 1, "a new auxiliary field is simply unknown and harmless". If Meta moves one post type into a new branch, the other branches still give posts. The run ends `ok`, `PostsFound > 0`, and the repetition signal never fires. The price is silent, partial data loss with no alert.
   Answer: Agree. The first plan edit kept a sibling branch with post identifiers in the fatal class. The step-back course goes further: the parser classes do not change at all, so every unknown sibling still blocks its raw and replays after a parser upgrade. Nothing is lost; only the run and the visit stop failing.
   User (owner proxy, 2026-11-10): resolved by the step-back course.

2. **Non-fatal raws become `Parsed = true` and never replay on their own.** Where: Contracts, and Verification "most become `Parsed = true`". Re-ingest runs only for `!Parsed`. When a later parser version learns the branch, those raws keep their missing posts unless someone remembers to run a manual replay. The price is that the "one repair loop" now has a hole you cannot see.
   Answer: Agree. The first plan edit widened `reparse --failed`. The step-back course removes the non-fatal class, so no raw becomes `Parsed = true` with missing posts, and the scheduler replay covers every blocked raw as today.
   User (owner proxy, 2026-11-10): resolved by the step-back course.

3. **The fatal class still depends on an allowlist.** Where: Target design 1, "any root whose context operation is not a known post operation" is non-fatal. If Meta renames an operation, a post feed becomes a harmless unknown. The plan removes one allowlist and keeps another that fails quietly in the same way.
   Answer: Agree. The operation rule is gone (finding 1). A response with zero posts and no explicit empty stays fatal whatever its operation. A renamed operation that no longer passes `Site.Gate` produces no raw; the run-level signal (finding 5) covers that.
   User (owner proxy, 2026-11-10): accepted as in finding 1.

4. **The migration destroys evidence and cannot be undone.** Where: Implementation step 5. It drops the attempt rows, the 35 diagnoses and the `Screenshot` decision before anyone runs the new design. The price is that a rollback after a bad step 1 has no old state to go back to.
   Answer: Disagree. A rollback of step 1 is a parser revert plus `reparse --failed`; it needs no recovery table. The 10 attempt rows are all `failed` (0 accepted), and the 35 diagnoses survive as the visit `Note` string. `Screenshot` stays (open question 2).
   User (owner proxy, 2026-11-10): drop the table and the columns. The brief allows the drop when the review agrees that the data has no value; the security reviewer also prefers less stored personal data.

5. **One plan holds too much.** Where: Implementation 1 to 8. A semantics change, about 600 deleted lines, a schema drop, config compatibility and a fixture swap ship together. Step 7 waits on open question 4, and step 8 is optional. The price is a large diff that is hard to review or bisect.
   Answer: Agree in part. Steps 7 and 8 are out (findings 6 and 7). The rest is one change: the recovery code exists only because of the fatal class, so the two cannot ship apart. Each step gets its own commit for bisect.
   User (owner proxy, 2026-11-10): steps 7 and 8 removed; one commit per step.

6. **"3 consecutive" is the wrong unit for low-volume operations.** Where: Target design 3, open question 1. A profile visit that runs weekly needs three weeks to alert. One lucky capture also resets the count. The price is slow or flapping alerts.
   Answer: Agree in part. The per-operation query is cut (finding 5). The remaining signal counts home collects, which run several times a day. A reset by one successful collect is the positive-evidence rule on purpose.
   User (owner proxy, 2026-11-10): accepted as in finding 5.

7. **The verification sample is too small to prove anything.** Where: Verification. Fourteen raws and one collect per platform cannot show that false negatives stay low. The plan has no measure for missed posts, so success counts as "fewer errors".
   Answer: Agree. The 14 raws are gone (parser 16 replayed them), so the replay check is now a no-op. Verification adds a `PostsFound` per home collect comparison for the first week, which the owner runs after merge.
   User (owner proxy, 2026-11-10): accepted; recorded under Verification.

## Findings

### 1. "Post-bearing operation" is undefined and would be a new allowlist: Major, FIX
Reviewers: neutral, minimalist. Where: Target design 1, step 1; `Site.cs` L11-15, L29-36
Finding: No list of post operations exists. `Site.Operation` reads only `fb_api_req_friendly_name`, so Instagram REST captures have a null operation. Under the plan's rule an Instagram feed with zero posts becomes non-fatal and `Parsed = true`. `ParseCore` also runs once per body, so the L136 check cannot use the operation.
Resolution: First edit: the rule became per response, with no operation names. After the step-back course the parser does not change at all (target design 1), so the question is moot.

### 2. Unknown shapes disappear from Manage: Major, FIX
Reviewers: neutral, ux, minimalist. Where: Target design 3; `Ingest.cs` L53; `RecoveryQuery.cs` L71, L94-102; `CaptureContext.cs` L51-52
Finding: A parsed raw is listed only when `Warning != null`; `Warning` comes from `Warnings`, not `Issues`. Compatibility needs `BlockedParserVersion == Version`. `Create` cannot produce impact `unknown`.
Resolution: First edit: the non-fatal unknown went into `Warnings` with impact `unknown` and a Compatibility row. After the step-back course there is no non-fatal class; a blocked raw keeps today's Compatibility row.

### 3. `parser_blocked` and the `captured` status break: Major, FIX
Reviewers: neutral, ux. Where: Target design 2; `Capture.cs` L66, L71, L132-134
Finding: Both rules use `listenerErrors > errorsBefore`. Without parser diagnostics in that counter a visit with one fatal raw shows `captured` and never gets `parser_blocked`. `parserErrors` would also count non-fatal unknowns, so a visit with one fatal root and one unknown sibling fails the equality test and the browser retries it.
Resolution: Target design 2 states the three rules: `captured` needs observed posts and no listener exception; `parserErrors` counts parser diagnostics as today; `parser_blocked` needs no observed posts and at least one diagnostic. Step 1 adds the tests.

### 4. A parser fix no longer reaches non-fatal raws: Major, FIX
Reviewers: ux, skeptic. Where: Target design 1, Verification; CLI `Program.cs` L152-159; `Scheduler.cs` L96
Finding: `reparse --failed` and the scheduler select `!Parsed` raws only. A raw with posts plus an unknown branch is `Parsed = true`, so a later parser fix never replays it.
Resolution: First edit: `reparse --failed` widened its selection. After the step-back course no raw with a diagnostic becomes `Parsed = true`, so the scheduler replay and `reparse --failed` reach every blocked raw as today. No change.

### 5. The per-operation health query has no data source and repeats a fatal signal: Major, FIX
Reviewers: minimalist, neutral, performance. Where: Target design 3, step 3, open question 1; `RecoveryQuery.cs` L28; `FeedQuery.cs` L156
Finding: `RawSnapshots` has no post count or operation column; the operation sits in `ContextJson` and the post counts in `ClassificationJson`, which the plan drops. A zero-post response is already fatal per raw. The query would run inside the Manage dot path.
Resolution: Target design 3 keeps only the run-level signal (three consecutive zero-post home collects per platform) with a constant threshold of 3, read from the small `Runs` table. Open question 1 is answered. The step-back course keeps this step.

### 6. Step 7 commits captures from the private instance through a leaky sanitizer: Major, ESCALATE
Reviewers: security, minimalist. Where: Step 7, open question 4; `tools/sanitize.py`; `docs/CODE-REVIEW-2026-10-23.md` L59
Finding: `sanitize.py` passes numbers under unlisted keys and dictionary keys unchanged, so a fixture can carry a real friend id into the public repository. The swap also does not change the fail-open behavior.
Resolution: Question: drop step 7 from this plan and keep the fabricated fixtures? Recommended: yes.
User (owner proxy, 2026-11-10): Yes. The brief says to keep the existing fixtures when no safe fixture can be produced. The fixture swap and the sanitizer fix become a separate plan.

### 7. Step 8 contradicts the contracts: Major, FIX
Reviewers: minimalist. Where: Step 8; Contracts; `CaptureHealth.cs` L9-21
Finding: The contracts keep `CaptureToken`, step 8 removes it. `CleanVisits` and about 56 references use it.
Resolution: Step 8 removed. Open question 3 answered: a later plan.

### 8. The config notice and the "one release" period are extra code: Nit, FIX
Reviewers: minimalist. Where: Target design 6, step 6
Finding: One private instance, no release cycle. A notice pipeline for one key is extra code.
Resolution: Target design 6: an ignored `JsonElement?` property with a comment at the line. The 04-instance doc marks the section ignored.

### 9. `parser_warnings` is write-only and `CollectionSummary` has no error text: Nit, FIX
Reviewers: minimalist. Where: Step 2; `CollectionSummary.cs` L12-16
Finding: Nothing reads `listener_errors` from `StatsJson`; `CollectionSummary` reads `scope`, `visited` and `incomplete` only.
Resolution: Target design 2 states that the `StatsJson` fields are for the log and the run row only, and that `CollectionSummary` does not change.

### 10. Screenshots for unrendered visits: Nit, FIX
Reviewers: minimalist. Where: Open question 2; `Capture.cs` L139
Finding: The smallest change deletes only the `VisualDiagnosis && rendered` clause and the `.page` write; the unrendered screenshot then stays with no other change.
Resolution: Adopted in target design 4 and open question 2.

### 11. The deletion list misses callers and texts: Nit, FIX
Reviewers: neutral, performance, minimalist, ux. Where: Step 4; `CaptureIndex.cs` L32; `ParserAudit.cs` L24; `ManagementRoutes.cs`; `RecoveryQuery.cs` L51, L73, L85, L89-101; `Capture.cs` L132, L137; `CoverageQuery.cs` L54
Finding: These call `CaptureRecovery.ParseSaved`, read recovery columns or say "model recovery" in operator texts.
Resolution: Added to target design 4 and step 3.

### 12. `ingestFailures > 0` still turns the run to `error`: Nit, FIX
Reviewers: neutral. Where: Target design 2; `Capture.cs` L205
Finding: A run with posts plus one fatal raw still ends `error`, against "run status follows the browser".
Resolution: Target design 2: `IngestResult` gets `Blocked` beside `Failed`. Only `Failed` (exceptions other than a fatal parse) and listener exceptions turn the run to `error`.

### 13. Deleting the writers leaves `.page`, `.recovery` and `diagnostics/visit-model/` files on disk: Major, ESCALATE
Reviewers: security. Where: Target design 4, step 4; `Maintenance.cs` L136, L157; `StorageCleanup.cs` L66
Finding: The files hold page text and extracted post content. `Maintenance` deletes `.recovery` with its raw but not `.page`; `diagnostics/visit-model/` is outside every cleanup.
Resolution: Question: add a one-time cleanup job, or keep the sidecars in the retention suffix lists and remove the model audit folder by hand? Recommended: the second; it adds no code.
User (owner proxy, 2026-11-10): The second. `Maintenance` keeps `.recovery` and gets `.page` in its suffix lists with a comment. The RUNBOOK names the one-time removal of `diagnostics/visit-model/`. The files are on the owner's own disk, not exposed.

### 14. The live evidence moved: Nit, FIX
Reviewers: none (owner proxy check on a copy of the live database, 2026-11-10). Where: Problem and evidence; Verification
Finding: 0 unparsed raws remain; parser version 16 replayed the 14. 36,703 raws, 2,864 parsed with a warning, 39 of 181 collect runs ended `error`, all 10 recovery attempts are `failed`.
Resolution: Table and Verification updated. The replay check is a no-op; the first-week `PostsFound` comparison replaces it.
