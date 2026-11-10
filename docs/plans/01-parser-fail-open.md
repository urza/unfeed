# Plan 01: Make parser uncertainty non-fatal and remove model-assisted capture recovery

Status: implemented 2026-11-10 on branch `01-parser-fail-open` (`.work/01-parser-fail-open/`). Reviewed the same day; the step-back course (CHANGE COURSE) was adopted: the parser classes do not change, the run and the visit stop failing on a blocked raw, and the model paths go.

## Outcome

A collect run that captured posts ends `ok` even when one response branch is unfamiliar. An unfamiliar shape blocks its raw, as today, and shows as a grouped compatibility notice. It no longer turns the run to `error`, the visit to `incomplete` or starts browser retries. Capture health is judged on positive evidence: home collects that find no posts, repeatedly. The model-assisted recovery path, the visual diagnosis path and their state are removed. Fixing the parser and letting the scheduler replay blocked raws stays the one repair loop.

## Problem and evidence

`Capture.cs` L205 sets the run status to `error` when `listenerErrors > 0`. `listenerErrors` counts parser `Diagnostics` (L66, L72). `PayloadParser` adds a diagnostic for an unrecognized sibling branch under `data` (L53-66) and for a root with no recognized posts and no explicit empty (L136). Recognition is an allowlist in code: `EnvelopeMetadata` (L79-91), `NonPostError` (L174-226), `InstagramPartialError` (L257), `FacebookAuxiliary` (L347) and `FacebookProfileHeader`. Each new Meta field is a code change and a version bump. The parser is at version 16 after six weeks.

To cope, the project added `CaptureRecovery` (model extraction with hash-bound overlays), `VisitDiagnosis` (vision model on screenshots), alert supersession and recovery dispositions.

Live instance, 2026-11-09 (counts updated 2026-11-10 from a copy of the database):

| Measure | Value |
|---|---|
| Raw captures | 36,703 (14 deleted) |
| Unparsed raws | 14 on 2026-11-09; 0 on 2026-11-10 after parser version 16 replayed them |
| Parsed raws with a warning | 108 Facebook, 2,756 Instagram |
| Collect runs | 181, of which 39 ended `error` |
| Model recovery attempts | 10, 0 accepted (all 10 rows `failed`) |
| Visual diagnoses written | 35, consumed only as a note string |
| Model-recovery code | 392 lines, plus 209 test lines |
| Schema cost | 1 table, 5 RawSnapshots columns, 4 TimelineVisits columns, 2 sidecar types, 8 config keys |

`CaptureRecovery.Extract` (L59-91) accepts only one simple text or image post per root. Platform changes break the complex branches, so the tool cannot reach the failures that occur. Every parser bump resets recovery state (`Ingest.cs` L44), so the same bytes are re-asked.

## Current behavior to keep in mind

- `ParsedCapture` (PayloadParser L6) carries `Posts`, `People`, `ExplicitEmpty`, `Diagnostics` (fatal), `Warnings`, `Parts` and `Issues`. `CaptureDiagnostic` (CaptureContext.cs L26) has `Code`, `Path`, `Impact`, `Evidence`, `Root`, `Fingerprint`. Codes: `unknown_response`, `upstream_error`, `invalid_json`. Impacts today: `content`, `unknown_or_content` (fatal), `auxiliary`, `partial` (warnings). `CaptureDiagnostics.Create` (CaptureContext.cs L51) computes the impact from the whole root, so a sibling diagnostic in a response with posts always gets `content`.
- `Ingest.File` stores posts first, then throws `FormatException` when `Diagnostics.Count > 0` (L68). The catch sets `BlockedParserVersion = Version` and `Parsed = false` (L75-76). `RawDiagnostics` rows are replaced on every parse (L49). `RawSnapshots.Warning` is filled from `parsed.Warnings` only (L53).
- `RecoveryQuery` (L71) lists a parsed raw only when `Warning != null`, files impact `auxiliary` or `partial` as Informational, and gives the Compatibility disposition only to `!Parsed && BlockedParserVersion == Version` (L102).
- `parser_blocked` (Capture.cs L133-137) stops browser retries for shape-only failures. It compares the `listenerErrors` delta with the `parserErrors` delta. `CaptureStatus = "captured"` (L132) requires `listenerErrors == errorsBefore`. `TimelineCoverage.cs` L37 and `CoverageQuery.cs` L54 consume `parser_blocked`.
- The scheduler never reads `Runs.Status == "error"`. Cooldown uses `LastRunFinishedAt`. Raw re-ingest is due when `!Parsed && BlockedParserVersion != Version` (Scheduler L96). `reparse --failed` (CLI Program.cs L152-159) selects `!Parsed` raws only.
- `Site.Operation` (Site.cs L11) reads only `fb_api_req_friendly_name`. Instagram REST captures (`/api/v1/feed/...`) have a null operation. There is no list of post operations in the code.
- `RecoveryQuery` (109 lines) mixes generic alerting (dispositions, judge and summary failures) with model-recovery fields (`RecoveryState`, `RecoveryAttempts`, `BackoffKey`).
- `CaptureIndex`, `ParserAudit`, `RawDiagnostics` fingerprint grouping and `reparse --failed` are the parts of the repair loop that work. `CaptureIndex.cs` L32, `ParserAudit.cs` L24 and `Ingest.cs` L39 call `CaptureRecovery.ParseSaved`, which applies the `.recovery` overlay.
- `Configuration.cs` L142 uses `UnmappedMemberHandling.Disallow`. Removing `CaptureRecoveryConfig` without a replacement property makes every existing `config.json` with a `capture_recovery` section fail to load.
- `Maintenance.cs` L136 and L157 delete the `.context`, `.recovery` and `.visits` sidecars with their raw or media file. `.page` is not in the lists.

## Target design

1. **The parser does not change.** `Ingest.File` stores the recognized posts before it throws on a diagnostic (L68), and the scheduler replays a blocked raw after a version bump (Scheduler L96). An unfamiliar sibling branch therefore loses no data today. Its cost is in `Capture.cs`: the run `error`, the `incomplete` visit and the browser retries. This plan fixes that cost and leaves `Diagnostics`, `Warnings`, `Issues` and the allowlists as they are. No version bump.
2. **Run and visit status follow the browser, not the parser.** `Capture.cs` keeps `status` as the browser produced it. `listenerErrors` counts only listener and save exceptions. `IngestResult` gets a `Blocked` count (a fatal parse, the raw waits for a parser upgrade) beside `Failed` (any other exception). Only `Failed > 0` or `listenerErrors > 0` turns an `ok` run into `error`. `StatsJson` gets `parser_blocked` (raws blocked at ingest), for the log and the run row only. A visit is `captured` when it rendered, observed posts for its target and had no listener exception. `parserErrors` counts parser diagnostics, as today. `parser_blocked` is set when the visit rendered, had no failure, observed no posts and had at least one fatal diagnostic. `CollectionSummary` reads `scope`, `visited` and `incomplete` only; it does not change.
3. **Health from repetition, one signal.** `CaptureHealth.SilentHomeCollects(db, platform)` counts the consecutive most recent finished home collects (`Kind == "collect"`, `Mode == "home"`, `Status` `ok` or `capped`) with `PostsFound == 0`. `RecoveryQuery.Read` emits one Attention issue per platform when the count reaches the constant `CaptureHealth.SilentCollects = 3`. There is no config key and no per-operation query: a zero-post response already blocks its raw and has its own Compatibility row. The run-level signal covers the case where a renamed operation no longer passes `Site.Gate`, so no raw exists.
4. **Delete the model paths.** Remove `CaptureRecovery.cs`, `RecoveryEvidence.cs`, `RecoveryInput.cs`, `VisitDiagnosis.cs`, `RecoveryAudit.razor`, `CaptureRecoveryTests.cs`, the `capture-recover` command, the `/manage/capture-recovery/{id}` route, the Manage panel, the `.recovery` overlay read (`CaptureRecovery.ParseSaved` in `Ingest.cs` L39, `CaptureIndex.cs` L32, `ParserAudit.cs` L24 become `PayloadParser.Parse`), the reset at `Ingest.cs` L44, the `VisualDiagnosis` clause and the `.page` write in `Capture.cs` L139-146, and the `Scheduler.cs` L97-98 and `Processing.cs` L266-272 calls. Screenshots for unrendered visits stay as plain evidence files; `TimelineVisits.Screenshot` stays. `Maintenance.cs` keeps `.recovery` in its suffix lists and gets `.page`, with a comment: the writers are gone, the files on disk are not, and they go with their raw or screenshot. The owner removes `diagnostics/visit-model/` by hand once (RUNBOOK).
5. **Schema cleanup.** One migration drops `CaptureRecoveryAttempts`; the RawSnapshots columns `RecoveryAttemptedAt`, `RecoveryAttempts`, `RecoveryError`, `RecoveryKey`, `RecoveryState` and the write-only `ClassificationJson`; the TimelineVisits columns `VisualAttemptedAt`, `VisualAttempts`, `VisualDiagnosisJson`, `VisualError`. The live data in these columns has no value: 10 attempts, 0 accepted, and the 35 diagnoses survive as the visit `Note` string.
6. **Configuration compatibility.** `FeedConfig` keeps a `CaptureRecovery` property typed `JsonElement?` that nothing reads, with a comment at the line: `UnmappedMemberHandling.Disallow` would otherwise reject the live `config.json`. No notice. `docs/04-instance.md` marks the section ignored.

## Contracts to preserve

- Binding rule 7, raw before parse. Unchanged.
- Partial observations never overwrite stored posts (`Ingest.cs` L96-110). Unchanged.
- A fatal parse keeps `BlockedParserVersion` and waits for a parser upgrade or explicit replay.
- An explicit empty timeline is still proven only by the existing authoritative evidence. Unknown content never becomes "successfully processed"; it becomes "unknown, no post content claimed".
- Existing `.context` and `.visits` sidecars, `CaptureToken`, `CaptureIndex`, `ParserAudit` and `CaptureHealth.Superseded` stay.

## Implementation sequence

1. Capture: stop counting parser diagnostics into `listenerErrors`. Keep `parserErrors` for them. Change the `captured` rule, the `parser_blocked` rule and the run-status rule as in target design 2. Add `Blocked` to `IngestResult`. Add `parser_blocked` to `StatsJson`. Update the texts at `Capture.cs` L132 and L137 and `CoverageQuery.cs` L54 (no "model"). Tests: a run with one unknown sibling beside posts ends `ok`, the visit is `captured` and the raw is blocked; a visit with one unknown root and no observed posts is `parser_blocked`; a blocked raw does not turn the run to `error`.
2. Health: add `CaptureHealth.SilentHomeCollects` and the Attention issue in `RecoveryQuery`. Test with synthetic runs: three consecutive zero-post home collects raise the issue; two do not; a person visit between them does not count.
3. Delete the model recovery and visual diagnosis code, routes, UI and tests listed in target design 4. Fix the hits in `CollectionReliabilityTests`, `ResilientCaptureTests`, `SchedulerTests`, `ManagementBrowserTests`, `StorageCleanup.cs` L72, `Management.cs` L12, L18, L65, `ManagementRoutes.cs`, `CoverageQuery.cs` L53-54, `RecoveryQuery.cs` L51, L73, L85, L89-101, CLI `Program.cs` L26, L75-83, `Maintenance.cs` suffix lists.
4. Migration for the schema cleanup. Run the migration test in `StorageTests`.
5. Configuration: the ignored `JsonElement?` property. Test: a config with a `capture_recovery` section loads, and its values are not reachable.

## Spec updates

- `docs/06-collectors.md` 6.10, 6.15, 6.17 (remove "Model-assisted capture recovery" L349-372; state that a blocked raw no longer fails the run or the visit; describe the repetition signal).
- `docs/05-data-model.md` RawSnapshots L126, TimelineVisits L237, 5.13 L459, "Capture classification and model-attempt provenance" L471.
- `docs/13-cli.md` L69 "Capture inspection and recovery commands": drop `capture-recover`.
- `docs/15-ui.md` L224-230: drop "Capture recovery groups and model audit".
- `docs/12-scheduler.md` L146-152: drop the capture-model queue.
- `docs/04-instance.md` L359-365: mark `capture_recovery` ignored.
- `docs/17-testing.md` L167, `docs/RUNBOOK.md` L58 and L71-79 (plus the one-time removal of `diagnostics/visit-model/`), `IMPLEMENTATION.md` L46.
- `docs/CAPTURE-RECOVERY-PLAN.md`: mark steps 4 onward superseded by this plan.
- Rebuild `docs/spec.html`.

## Verification

- Fast suite and browser suite pass. Fixture tests above pass.
- Copy of the live database: the migration runs, and `reparse --failed` selects 0 raws (0 unparsed raws on 2026-11-10).
- The owner, after merge: one live collect per platform ends `ok` with `parser_blocked` possibly above zero and no Attention issue. Compare `PostsFound` per home collect with the week before; a drop with `parser_blocked > 0` points at a shape the parser must learn.

## Decisions on the open questions (review, 2026-11-10)

1. Threshold: a constant 3 consecutive zero-post home collects per platform. No time window, no config key. Home collects run several times a day, so three in a row is hours, not weeks.
2. Screenshots for unrendered visits stay as plain evidence files. Only the model clause and the `.page` write go.
3. Supersession and `CaptureToken` stay. A later plan can remove `CaptureHealth.Superseded` after the new run and visit rules have run in production.
4. No real fixtures in this plan. `tools/sanitize.py` passes numbers under unlisted keys and dictionary keys unchanged (`docs/CODE-REVIEW-2026-10-23.md` L59), so a sanitized capture can still carry a friend id. The fixture swap and the sanitizer fix are a separate plan.

## Out of scope

Checkpoint detection (review finding 6), like-back, the scheduler tick, cookie export, the fixture swap and the sanitizer fix (open question 4), supersession removal (open question 3).

## Notes from implement

Implemented on 2026-11-10 on branch `01-parser-fail-open`. Differences from the text above:

- `IngestResult.Blocked` is a subset of `Failed`, not a separate outcome: a blocked parse returns `Failed = 1, Blocked = 1`. Existing tests and the CLI `reparse` exit code keep their meaning, and `Capture.Collect` subtracts `Blocked` from the failures that turn the run to `error`. The process worker still counts a blocked replay as a failed stage, as before.
- `Maintenance` did not get `.page` in its suffix lists. The `.page` sidecar sits next to the screenshot under `media/diagnostics/<platform>/<run dir>/`, and raw retention deletes that directory with the run directory (`Maintenance.cs` L149). `.recovery` stays in the raw list with a comment.
- `ParserAudit` reports `Native` only. The `Effective` field was the `.recovery` overlay view and would now repeat `Native`.
- The run and visit status rules in `Capture.Collect` have no offline test. They run inside the Playwright flow, and the existing suite never tested them either. The reviewer and the owner check them on the first live collects.
- The migration is `20261110112057_RemoveCaptureRecovery`, scaffolded with `dotnet ef`. It drops the table, the index on `RecoveryKey`, six `RawSnapshots` columns and four `TimelineVisits` columns.
- The `SchedulerTests` case `CaptureRecoveryHonorsPauseReloginAndStartupBackoff` tested the sweep recovery dispatch, not the model queue. It is renamed `SweepRecoveryHonorsPauseReloginAndStartupBackoff`.
- Live-copy check (copy of `data/feed.db` with its WAL, plus the instance files, in the scratchpad): the CLI `status` applied `RemoveCaptureRecovery`, the live `config.json` with its `capture_recovery` section loaded, and the copy holds 36,703 raws with 0 unparsed. `reparse --failed` would select nothing.
- Checks: 391 fast tests and 28 browser tests pass; `./build.sh` passes.
- Code review fixes (`.work/01-parser-fail-open/code-review.md`): `SilentHomeCollects` skips person collects through `StatsJson.scope` and returns the run ids of the streak, the Manage page shows the alert under its own `silent` category with a link to the latest silent run, the visit note counts every blocked response while `parser_blocked` still needs an unrecognized shape, and `ingest=` is `done` or `failed` again.

