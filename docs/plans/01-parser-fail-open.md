# Plan 01: Make parser uncertainty non-fatal and remove model-assisted capture recovery

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

A collect run that captured posts ends `ok` even when one response branch is unfamiliar. Unfamiliar shapes become grouped compatibility notices, not run errors. Capture health is judged on positive evidence: a post-bearing operation that returns no posts and no explicit empty, repeatedly. The model-assisted recovery path, the visual diagnosis path and their state are removed. Fixing the parser and replaying raws stays the one repair loop.

## Problem and evidence

`Capture.cs` L205 sets the run status to `error` when `listenerErrors > 0`. `listenerErrors` counts parser `Diagnostics` (L66, L72). `PayloadParser` adds a diagnostic for an unrecognized sibling branch under `data` (L53-66) and for a root with no recognized posts and no explicit empty (L136). Recognition is an allowlist in code: `EnvelopeMetadata` (L79-91), `NonPostError` (L174-226), `InstagramPartialError` (L257), `FacebookAuxiliary` (L347) and `FacebookProfileHeader`. Each new Meta field is a code change and a version bump. The parser is at version 16 after six weeks.

To cope, the project added `CaptureRecovery` (model extraction with hash-bound overlays), `VisitDiagnosis` (vision model on screenshots), alert supersession and recovery dispositions.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| Raw captures | 35,029 |
| Unparsed raws | 14 |
| Model recovery attempts | 10, 0 accepted (7 rejected, 3 endpoint unavailable) |
| Visual diagnoses written | 35, consumed only as a note string |
| Model-recovery code | 392 lines, plus 209 test lines |
| Schema cost | 1 table, 5 RawSnapshots columns, 4 TimelineVisits columns, 2 sidecar types, 8 config keys |

`CaptureRecovery.Extract` (L59-91) accepts only one simple text or image post per root. Platform changes break the complex branches, so the tool cannot reach the failures that occur. Every parser bump resets recovery state (`Ingest.cs` L44), so the same bytes are re-asked.

## Current behavior to keep in mind

- `ParsedCapture` (PayloadParser L6) carries `Posts`, `People`, `ExplicitEmpty`, `Diagnostics` (fatal), `Warnings`, `Parts` and `Issues`. `CaptureDiagnostic` (CaptureContext.cs L26) has `Code`, `Path`, `Impact`, `Evidence`, `Root`, `Fingerprint`. Codes: `unknown_response`, `upstream_error`, `invalid_json`.
- `Ingest.File` stores posts first, then throws `FormatException` when `Diagnostics.Count > 0` (L68). The catch sets `BlockedParserVersion = Version` and `Parsed = false` (L75-76). `RawDiagnostics` rows are replaced on every parse (L49).
- `parser_blocked` (Capture.cs L133-137) stops browser retries for shape-only failures. `TimelineCoverage.cs` L37 and `CoverageQuery.cs` L54 consume it.
- The scheduler never reads `Runs.Status == "error"`. Cooldown uses `LastRunFinishedAt`. Raw re-ingest is due when `!Parsed && BlockedParserVersion != Version` (Scheduler L96).
- `RecoveryQuery` (109 lines) mixes generic alerting (dispositions, judge and summary failures) with model-recovery fields (`RecoveryState`, `RecoveryAttempts`, `BackoffKey`).
- `CaptureIndex`, `ParserAudit`, `RawDiagnostics` fingerprint grouping and `reparse --failed` are the parts of the repair loop that work.
- `Configuration.cs` L142 uses `UnmappedMemberHandling.Disallow`. Removing `CaptureRecoveryConfig` makes every existing `config.json` with a `capture_recovery` section fail to load.

## Target design

1. **Two outcome classes in the parser.** Fatal: `invalid_json`; `upstream_error` on a post branch; and "post-bearing operation with zero posts and no explicit empty". Non-fatal: an unrecognized sibling branch, an unknown root in a response that also yielded posts, and any root whose context operation is not a known post operation. Non-fatal outcomes become `Issues` with impact `unknown` and leave `Diagnostics` empty. The allowlists in `NonPostError`, `EnvelopeMetadata` and `FacebookAuxiliary` stop growing: a new auxiliary field is simply unknown and harmless.
2. **Run status follows the browser, not the parser.** `Capture.cs` counts parser issues into `StatsJson.parser_warnings` and keeps `status` as the browser produced it. `listenerErrors` counts only listener and save exceptions. `parser_blocked` stays for the fatal class.
3. **Health from repetition.** `CaptureHealth` gains two queries over existing tables: per platform and operation, the count of consecutive captures with zero posts and no explicit empty; per platform, the count of consecutive home collects with `PostsFound = 0`. `RecoveryQuery` emits an Attention issue when either passes a threshold (default 3). Unknown-shape fingerprints appear under the Compatibility disposition with affected-capture counts, as today.
4. **Delete the model paths.** Remove `CaptureRecovery.cs`, `RecoveryEvidence.cs`, `RecoveryInput.cs`, `VisitDiagnosis.cs`, `RecoveryAudit.razor`, `CaptureRecoveryTests.cs`, the `capture-recover` command, the `/manage/capture-recovery/{id}` route, the Manage panel, the `.recovery` overlay read in `Ingest.cs` L39, the reset at L44, the `VisualDiagnosis` branch in `Capture.cs` L139-146, the `.page` sidecar, and the `Scheduler.cs` L97-98 and `Processing.cs` L266, L272 calls.
5. **Schema cleanup.** One migration drops `CaptureRecoveryAttempts`; the RawSnapshots columns `RecoveryAttemptedAt`, `RecoveryAttempts`, `RecoveryError`, `RecoveryKey`, `RecoveryState` and the write-only `ClassificationJson`; the TimelineVisits columns `VisualAttemptedAt`, `VisualAttempts`, `VisualDiagnosisJson`, `VisualError`. `Screenshot` stays only if the reviewer wants screenshots for unrendered visits (open question 2).
6. **Configuration compatibility.** Keep `capture_recovery` as an accepted, ignored section for one release. Loading it produces an Informational notice "capture_recovery is no longer used; remove it from config.json".

## Contracts to preserve

- Binding rule 7, raw before parse. Unchanged.
- Partial observations never overwrite stored posts (`Ingest.cs` L96-110). Unchanged.
- A fatal parse keeps `BlockedParserVersion` and waits for a parser upgrade or explicit replay.
- An explicit empty timeline is still proven only by the existing authoritative evidence. Unknown content never becomes "successfully processed"; it becomes "unknown, no post content claimed".
- Existing `.context` and `.visits` sidecars, `CaptureToken`, `CaptureIndex` and `ParserAudit` stay.

## Implementation sequence

1. Parser: split outcomes. Change L53-66 and L136 so that only the fatal class reaches `Diagnostics`. Use `CaptureContext.Operation` to decide "post-bearing operation". Add fixture tests: unknown sibling with posts yields posts, zero `Diagnostics`, one `unknown` issue; known post operation with zero posts and no explicit empty is fatal; invalid JSON is fatal. Bump `Version` to 17.
2. Capture: stop counting parser issues into `listenerErrors`. Add `parser_warnings` to `StatsJson`. Update `CollectionSummary` and `RecoveryPresentation` texts. Test: a run with one unknown sibling ends `ok`.
3. Health: add the two repetition queries to `CaptureHealth` and the Attention issue to `RecoveryQuery`. Test with synthetic raws: three consecutive zero-post captures of one operation raise the issue; one does not.
4. Delete the model recovery and visual diagnosis code, routes, UI and tests listed in target design item 4. Fix the hits in `CollectionReliabilityTests`, `ResilientCaptureTests`, `SchedulerTests`, `ManagementBrowserTests`, `StorageCleanup.cs` L72, `Management.cs` L12, L18, L65, `CoverageQuery.cs` L53-54, `Program.cs` L26, L75-83.
5. Migration for the schema cleanup. Run the migration test in `StorageTests`.
6. Configuration: ignored section plus notice. Test: a config with `capture_recovery` loads.
7. Replace the fabricated fixtures with sanitized real captures through `tools/sanitize.py`, one per story type, as 6.15 requires. Review each fixture for identifiers before commit.
8. Optional, after review: remove `CaptureHealth.Superseded` and `CaptureToken` on raws. With non-fatal warnings, the active raw alerts shrink to the fatal class and the 7-day age-out in `RecoveryQuery` (`RawIssueDays`) is enough.

## Spec updates

- `docs/06-collectors.md` 6.10, 6.15, 6.17 (remove "Model-assisted capture recovery" L349-372 and the per-version warning subsections; describe the two outcome classes and the repetition signal).
- `docs/05-data-model.md` RawSnapshots L126, TimelineVisits L237, 5.13 L459, "Capture classification and model-attempt provenance" L471.
- `docs/13-cli.md` L69 "Capture inspection and recovery commands": drop `capture-recover`.
- `docs/15-ui.md` L224-230: drop "Capture recovery groups and model audit".
- `docs/12-scheduler.md` L146-152: drop the capture-model queue.
- `docs/04-instance.md` L359-365: mark `capture_recovery` ignored.
- `docs/17-testing.md` L167, `docs/RUNBOOK.md` L58 and L71-79, `IMPLEMENTATION.md` L46.
- `docs/CAPTURE-RECOVERY-PLAN.md`: mark steps 4 onward superseded by this plan.
- Rebuild `docs/spec.html`.

## Verification

- Fast suite passes. Fixture tests above pass.
- Private instance: replay the 14 unparsed raws with `reparse --failed` after step 1 and record how many become fatal versus unknown. Expected: most become `Parsed = true` with an `unknown` issue.
- One live collect per platform ends `ok` with `parser_warnings` possibly above zero and no Attention issue.

## Open questions for the reviewer

1. Threshold for the repetition signal: 3 consecutive zero-post captures per operation, or a time window?
2. Keep screenshots for unrendered visits as plain evidence files (no model), or drop them with `VisitDiagnosis`?
3. Step 8: remove supersession now or in a later plan?
4. Step 7 needs a real capture per story type from the private instance. Who sanitizes and reviews them?

## Out of scope

Checkpoint detection (review finding 6), like-back, the scheduler tick, cookie export.
