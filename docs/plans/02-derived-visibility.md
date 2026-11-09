# Plan 02: Derive visibility from stored decisions; version only prompt inputs

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

A change to the hide threshold, the always-show bypasses or a muted person takes effect on all history without a model call. The verdict version changes only when the model would see different input. Manage refilter and CLI refilter produce the same visibility for the same post.

## Problem and evidence

`Prompts.ConfigurationHash` (`src/Feed.Core/Application/Prompts.cs` L38-44) hashes `llm.threshold`, `filters.always_show_bypasses`, every platform's `close_friends`, and the whole `video_context` section. The threshold is never in the prompt. It is applied once, when a fresh verdict lands (`Processing.cs` L244). So a threshold edit reaches history only through `rescore --all`.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| Posts with a verdict | 7,880 |
| Verdicts marked stale on 10-30 by a configuration-only change | 4,877 |
| Distinct PrefsVersion values in six weeks | 12 |
| Posts with score below threshold and visible | 78 |

Two refilter paths exist with different rules. Manage refilter (`src/Feed.Web/Management.cs` L264-280) re-applies the low-score `llm` hide at L273. CLI refilter (`src/Feed.Cli/Program.cs` L176-181) does not. After an unmute through the CLI, a low-scored post stays visible until Manage runs a refilter. Spec 08 "Management controls" L75 forbids this.

`Filters.Restrict` (`src/Feed.Core/Domain/Identity.cs` L79-83) overwrites `CategoriesJson` in place. A category dropped for a non-close-friend cannot return without a rescore.

## Current behavior to keep in mind

- `Filters.Evaluate` (Identity.cs L64-72): first match wins, structural, whitelist, keyword, mute.
- `Filters.Apply` (L73-78): a gate replaces an `llm` hide; no gate clears a deterministic hide; an `llm` hide clears only under `Shield("llm")`.
- `Post.SetHidden` / `ClearHidden` (`Entities.cs` L30-31) bump `VisibilityRevision`. `thumbs` is reserved and cannot be set.
- `Actions.Unhide` (`Actions.cs` L34) clears all four visibility columns regardless of owner.
- Automatic `process` never selects a post because its PrefsVersion is stale (`Processing.cs` L113-121). Only `rescore --all` and `rescore --author` do.
- `Version(s, textOnly)` (Prompts.cs L46) is `prefsHash.taxonomyHash.gN.mConfigurationHash`.

## Target design

1. **One pure function decides visibility.** Add `Visibility.Decide(post, rules, config)` in Domain. Order: owner-reserved owners (`other`) stay as stored; then the first deterministic gate from `Filters.Evaluate`; then `llm` when `post.Judged && LlmScore < Threshold && !Shield("llm")`; else visible. The function returns owner and reason. `Filters.Apply` becomes a thin caller that writes the result through `SetHidden`/`ClearHidden` only when owner or reason changed.
2. **One application operation reapplies it.** Add `Visibility.Reapply(db, scope, rules, config)` in Application. Scope is all, platform, author, post ids, or since. Ingest, verdict apply, Manage refilter, CLI refilter, Person save and identity merge all call it. Delete the inline loops in Management.cs L264-280 and Program.cs L176-181.
3. **Split the version.** `ConfigurationHash` keeps model, endpoints, temperature, token limits, vision settings, vision image count, preprocessing string and summary thinking. Remove threshold, bypasses, close friends and video_context from it. These are decision inputs or per-post inputs, not global prompt inputs.
4. **Per-post prompt facts get their own trigger.** Close-friend membership and always-show names enter the prompt for one author. A Manage Person save or a preferences always-show edit queues `rescore --author` for the affected authors, not a global version bump. Video context text is already part of `VerdictInputHash`; a targeted `rescore --video-without-context` command covers it once the tool works (see plan 10).
5. **Threshold save reapplies without the model.** Manage `SaveSettings` action `processing` (Management.cs L205-208) calls `Reapply(all)`. This is an indexed update over judged posts and needs no lock on the model.
6. **Keep model categories raw.** Store the model's categories unrestricted in `CategoriesJson`. Apply `Restrict` where categories are read (FeedQuery membership and card trail). This lets a close-friend change restore categories without a rescore. If the read-path cost is a concern, store both columns: `CategoriesJson` raw and `EffectiveCategoriesJson` written by `Reapply`.

## Contracts to preserve

- Binding rule 4: the three columns on the post row remain the single visibility authority. `Decide` writes them; queries still never re-apply filter logic.
- Binding rule 12: fail-open. An unjudged post is never hidden by `llm`.
- Binding rule 13: a new post commits with its deterministic result in one transaction. `Reapply` inside ingest keeps that.
- Owner unhide stays as today: it clears the columns and bumps `VisibilityRevision`. A later refilter may re-apply a gate. See open question 1.
- Conditional commits on `ContentRevision` and `VisibilityRevision` stay unchanged.

## Implementation sequence

1. Add `Visibility.Decide` with unit tests that enumerate every owner outcome, including shield cases and the `other` passthrough. No behavior change yet.
2. Route `Filters.Apply`, Processing L244 and Manage L273 through `Decide`. Run the suite. Behavior is identical by construction.
3. Add `Visibility.Reapply` and move Manage refilter, CLI refilter and Person save onto it. Add a test that runs CLI refilter after an unmute and asserts the low-score hide returns. Fix the `ManagementTests.UnmutingOneNameMatch...` expectations if they encode the old split.
4. Change `ConfigurationHash`. Keep the old function for one release as `LegacyConfigurationHash`. At `FeedDb.Initialize`, for each post whose `PrefsVersion` equals the current legacy version, stamp the new version. Record the step in Kv `prefs:version-format` so it runs once. Without this step every verdict shows the "older policy" trail line.
5. Wire the threshold save to `Reapply(all)`. Add a test: lower the threshold, save, assert hides appear with no model request.
6. Add per-author rescore queuing from Person save and always-show edits. Reuse the existing `rescore --author` path. Add a test that only that author's posts become due.
7. Optional, after review: raw categories plus read-time `Restrict` (target design item 6).

## Spec updates

- `docs/08-filters.md` 8.7, 8.8 and "Management controls": describe `Decide` as the single engine and state that both refilter paths share it.
- `docs/09-llm.md` 9.3: list the new version inputs and the per-author rescore trigger.
- `docs/05-data-model.md` 5.5 and L98: the derived rule and the one-time version restamp.
- `docs/13-cli.md` 13.2: `refilter` semantics now match Manage.
- `docs/02-overview.md` rule 6 is unchanged. Note under 2.5 that threshold is tuning, not a verdict input.

## Verification

- Unit tests above. Existing `ProcessingTests`, `ModelSafetyTests`, `ManagementTests` and `ConfigurationTests.VisibilityOwnsDatesAndRevisions` must pass unchanged, except the one noted in step 3.
- Private instance check: after upgrade, the count of "older policy" cards is unchanged, and a threshold change from 4 to 5 hides the 78 known posts in one Manage save with zero model requests in the log.

## Open questions for the reviewer

1. Should owner unhide become a durable marker (`OwnerUnhiddenAt`) that `Decide` respects, so a later refilter cannot re-hide a post the owner released? Today it is not durable. This changes 8.9.
2. Should `thumbs` leave `HideOwners`? Rule 11 says thumbs never filter, and the code already forbids the value.
3. Target design item 6 doubles category storage or adds read-path work. Choose one.
4. `Reapply(all)` over 10k posts inside a web request: acceptable, or queue it as a child run like other heavy work? Today Manage refilter already runs in the request.

## Out of scope

Prompt content, judge quality measurement (plan 09), video context repair (plan 10), the fairness and rotation machinery in `Processing.Run`.
