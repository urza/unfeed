# Round 1 (2026-11-10)

Target: `git diff 636da66...HEAD` (branch `01-parser-fail-open`), spec `docs/plans/01-parser-fail-open.md`

Reviewers: neutral, minimalist, performance (reason: a new query in the Manage dot path), ux (reason: operator texts, the CLI summary line and the docs changed)

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The plan fixes the real cost (blocked raws turned runs into `error`, marked visits `incomplete` and started useless browser retries), and it deletes about 600 lines of model recovery code that had 0 accepted results in 10 attempts. The parser and the replay loop stay unchanged, so no data or repair path is lost.

The step-back agent weighed the one new mechanism, the silent-home-collect alert, against not having it. Before this change, a run that ended `error` was the only run-level alarm, and this branch removes it. The alert is the cheapest replacement: about 15 lines that read the `Runs` table, plus one presentation category. The ignored `JsonElement?` config property costs one line, which is less than a config migration would cost. Nothing in the diff needs to be simpler or reverted.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. A person collect counts as a home collect in the health signal: Major, FIX
Reviewers: neutral. Where: `CaptureHealth.SilentHomeCollects`; `Scheduler.cs` L48; `Ownership.cs` L53
Finding: A person collect runs with `Kind = "collect"` and `Mode = "home"`, and the `Run` row stores no person. A friend collect with posts between two silent home collects reset the count; three quiet-friend collects raised a false alert. The plan's test case was missing.
Resolution: The query skips runs whose `StatsJson` carries `"scope":"person"`, with a comment at the line that says why `Mode` alone is not enough. The test now puts a person run with posts inside the silent streak and expects the streak to stay.

### 2. The health alert showed under "Saved capture needs processing" with its message hidden: Major, FIX
Reviewers: ux. Where: `RecoveryQuery.cs` L73; `RecoveryPresentation.cs` L8-21; `RecoveryDetails.razor` L23-27
Finding: The issue fell into the `capture` category, so the owner saw a capture title, a capture explanation, "Collection association unavailable" and the real text only under "Technical details".
Resolution: `RecoveryPresentation` gets the `silent` category with its own title and explanation. `SilentHomeCollects` returns the run ids of the streak; the issue carries `RunId` of the latest silent run, so the run link shows. `RecoveryPresentationTests` covers the category.

### 3. A visit with a blocked GraphQL-error or invalid-JSON response got a wrong note: Nit, FIX
Reviewers: ux, neutral. Where: `Capture.cs` L66, L72, L134; `docs/06-collectors.md` step 4
Finding: `parserErrors` counts `unknown_response` only, so a visit whose only blocked response is an upstream error or invalid JSON got the "no post payload matched" note. The neutral reviewer noted that the 06 text promised `parser_blocked` for every blocked response, which the code does not do; the browser retry is the better behavior for a possibly transient upstream error.
Resolution: A `blockedResponses` counter (every diagnostic) feeds the visit note; `parserErrors` (unrecognized shapes) still decides `parser_blocked`. The comment at the counters says why. Chapters 05 and 06 state that only an unrecognized shape sets `parser_blocked`.

### 4. `ingest=blocked` repeats `parser_blocked`: Nit, FIX
Reviewers: minimalist. Where: `Capture.cs` L205; `docs/06-collectors.md` L129
Finding: The new `ingest` value carried no fact that `parser_blocked > 0` does not give, and nothing reads `ingest`.
Resolution: `ingest` is `done` or `failed` again; the chapter lists the two values.

### 5. Performance: All good
Reviewers: performance. Checked: the health query reads three rows per enabled platform through the `(Platform, Status)` index, outside the per-raw loop; `RecoveryQuery` does less work than before; the migration is one table rebuild at upgrade; nothing new grows.
