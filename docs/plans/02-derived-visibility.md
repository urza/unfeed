# Plan 02: Derive visibility from stored decisions; version only prompt inputs

Status: proposed 2026-11-09 from the step-back review. Reviewed and reduced on 2026-11-09 (report: `.work/02-derived-visibility/plan-review.md`). Implemented on 2026-11-10 on branch `02-derived-visibility`; see "Notes from implement".

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
| Posts with score below threshold and visible | 78 (all from the one always-show author; the `llm` bypass shields them, so this is correct behavior, checked 2026-11-09) |

Two refilter paths exist with different rules. Manage refilter (`src/Feed.Web/Management.cs` L264-280) re-applies the low-score `llm` hide at L273. CLI refilter (`src/Feed.Cli/Program.cs` L176-181) does not. After an unmute through the CLI, a low-scored post stays visible until Manage runs a refilter. Spec 08 "Management controls" L75 forbids this.

`Filters.Restrict` (`src/Feed.Core/Domain/Identity.cs` L79-83) overwrites `CategoriesJson` in place. A category dropped for a non-close-friend cannot return without a rescore.

## Current behavior to keep in mind

- `Filters.Evaluate` (Identity.cs L64-72): first match wins, structural, whitelist, keyword, mute.
- `Filters.Apply` (L73-78): a matching gate replaces an `llm` hide; when no gate matches, a deterministic hide clears; an `llm` hide clears only under `Shield("llm")`.
- `Post.SetHidden` / `ClearHidden` (`Entities.cs` L30-31) bump `VisibilityRevision`. `thumbs` is reserved and cannot be set.
- `Actions.Unhide` (`Actions.cs` L34) clears all four visibility columns regardless of owner.
- Automatic `process` never selects a post because its PrefsVersion is stale (`Processing.cs` L113-121). Only `rescore --all` and `rescore --author` do.
- `Version(s, textOnly)` (Prompts.cs L46) is `prefsHash.taxonomyHash.gN.mConfigurationHash`.

## Target design

1. **One pure function decides visibility.** Add `Visibility.Decide(post, rules, config)` in Domain. Order: owner-reserved owners (`other`) stay as stored; then the first deterministic gate from `Filters.Evaluate`; then `llm` when `post.Judged && LlmScore < Threshold && !Shield("llm")` and the owner did not unhide this content (see item 6); else visible. The function returns owner and reason. `Filters.Apply` becomes a thin caller that writes the result through `SetHidden`/`ClearHidden` only when owner or reason changed.
2. **One application operation reapplies it.** Add `Visibility.Reapply(db, posts, snapshot)` in Application. `posts` is an `IQueryable<Post>` filter that the caller builds. Manage refilter and Person save pass all posts. CLI refilter passes its platform, since and post-id filter. For each post, `Reapply` calls `Decide`, writes `CategoriesJson` through `Filters.Restrict`, and increments `VisibilityRevision`. Both loops do these three things today. The increment on every post in scope is deliberate: an in-flight verdict carries the old close-friend rules (`w.Rules`, Processing.cs L243), and only the revision check at L238 supersedes it. Delete the inline loops in Management.cs L264-280 and Program.cs L176-181. Ingest (Ingest.cs L115), verdict apply (Processing.cs L244) and identity merge (IdentityStore.cs L53) each hold one loaded post inside their own transaction. They call `Decide` through `Filters.Apply`, not `Reapply`.
3. **Take decision inputs out of the version.** Remove `llm.threshold` from `ConfigurationHash`. Replace the bypass list with one fact: `bypassLlm = AlwaysShowBypasses.Contains("llm")`. `Prompts.Policy` (Prompts.cs L32) adds the always-show sentence to every prompt only when this flag is set. Keep close friends and `video_context` in the hash. The model sees both (`author_close_friend`, Prompts.cs L71), so a change to them must mark old verdicts as stale.
4. **Close-friend edits keep today's behavior.** A close-friend change gives every verdict the "older policy" trail line, and the Person save message tells the owner to rescore (Management.cs L251). This plan adds no rescore queue.
5. **Threshold save reapplies without the model.** The Manage `SaveSettings` action `processing` (Management.cs L205-208) always saves. It never fails with "Processing is active", so the owner can change `enabled` or `timeout_seconds` during a run. When the threshold value changed, the action sets `management:refilter_pending` and tries the processing and ingest locks, as the other refilter saves do (L147-150). If it gets them, it runs `Reapply` over all posts in the request. If a lock is busy, `ResumeRefilter` (L256, called from Scheduler.cs L33) runs it later. A run that is active at save time keeps its start threshold (Processing.cs L244); the later `Reapply` corrects the verdicts that land after the save. A save that does not change the threshold does not reapply.
6. **Owner unhide is durable against the model.** Add a nullable column `Post.OwnerUnhiddenRevision`. `Actions.Unhide` (Actions.cs L34) sets it to the post's `ContentRevision`. `Decide` skips the `llm` hide while `OwnerUnhiddenRevision == ContentRevision`. A content change increments `ContentRevision`, so the marker expires with no clear code. A manual hide (`other`) wins because `Decide` checks reserved owners first. Deterministic gates stay as today: a mute or keyword can hide the post again. Decided by the owner in review (2026-11-09).

## Contracts to preserve

- Binding rule 4: the three columns on the post row remain the single visibility authority. `Decide` writes them; queries still never re-apply filter logic.
- Binding rule 12: fail-open. An unjudged post is never hidden by `llm`.
- Binding rule 13: a new post commits with its deterministic result in one transaction. Ingest calls `Decide` inside that transaction, as it calls `Filters.Apply` today.
- Owner unhide still clears the four columns and bumps `VisibilityRevision`. It now also sets `OwnerUnhiddenRevision` (target design item 6). This changes spec 8.9: a refilter or a rescore no longer re-applies the `llm` hide to unchanged content.
- Conditional commits on `ContentRevision` and `VisibilityRevision` stay unchanged.

## Implementation sequence

1. Add `Visibility.Decide` with unit tests that enumerate every owner outcome, including shield cases and the `other` passthrough. No behavior change yet.
2. Route `Filters.Apply`, Processing L244 and Manage L273 through `Decide`. Run the suite. Three results change on purpose. Add a test for each.
   - A post has an `llm` hide and its content changes at ingest. Today `Filters.Apply` keeps the hide. `Decide` makes the post visible until the model judges it again, because `Judged` is false (binding rule 12).
   - A deterministic gate clears at ingest or identity merge. Today only Manage re-applies a low-score `llm` hide (L273). `Decide` re-applies it on every path.
   - A post has an `llm` hide and its stored score is at or above the threshold. Today only a fresh verdict clears the hide. `Decide` clears it.
3. Add `Visibility.Reapply` and move Manage refilter, CLI refilter and Person save onto it. Add a test that runs CLI refilter after an unmute and asserts the low-score hide returns. Fix the `ManagementTests.UnmutingOneNameMatch...` expectations if they encode the old split. Then measure `Reapply` over all posts on a copy of the live database. `RuleContext.Shield` and `CloseFriend` (Identity.cs L57-58) resolve every always-show and close-friend entry against all authors for each post, and `Identity.Keys` parses `RefsJson` each time. If the run takes more than 2 seconds, resolve the shown and close-friend author ids once per `Reapply` call and give them to `RuleContext`.
4. Change `ConfigurationHash`. Do a one-time restamp at CLI and Web startup, after the snapshot loads. `FeedDb.Initialize` (FeedDb.cs L47) cannot do it, because it has no snapshot. Compute the old version for both modes, `Version(s, false)` and `Version(s, true)`. For each post whose `PrefsVersion` equals one of them, stamp the matching new version. Keep the old hash as a private helper inside the restamp, with a comment that gives the reason, and delete both together later. Record the step in Kv `prefs:version-format` so it runs once. Without this step every verdict shows the "older policy" trail line.
5. Add `OwnerUnhiddenRevision` with a migration (target design item 6). Tests: unhide a low-score post, run `Reapply`, assert it stays visible; change its content, judge it low again, assert the `llm` hide returns; mute its author, assert the mute hides it. This step comes before the threshold save, because that save makes `Reapply` a common event.
6. Wire the threshold save as target design item 5 describes. Change the confirmation text at Management.cs L214, which says "New runs use these settings". When `Reapply` ran, return the counts, for example "Saved. Threshold reapplied: N hidden, M shown, no model calls." When a lock was busy, say that existing posts follow when the active run ends. Tests: lower the threshold, save, assert hides appear with no model request; save with the processing lock held, assert the save succeeds, the pending key is set, and `ResumeRefilter` applies the hides after the lock is free; save with an unchanged threshold, assert no `VisibilityRevision` changes.

## Spec updates

- `docs/08-filters.md` 8.7, 8.8 and "Management controls": describe `Decide` as the single engine and state that both refilter paths share it. 8.9: an owner unhide holds against the `llm` hide until the content changes.
- `docs/05-data-model.md`: the `OwnerUnhiddenRevision` column.
- `docs/09-llm.md` 9.3: list the new version inputs.
- `docs/05-data-model.md` 5.5 and L98: the derived rule and the one-time version restamp.
- `docs/13-cli.md` 13.2: `refilter` rules now match Manage. The default scope does not: a plain `refilter` covers posts captured since the last collect (Program.cs L178). Tell the reader to pass `--all` after a rule change.
- `docs/02-overview.md` rule 6 is unchanged. Note under 2.5 that threshold is tuning, not a verdict input.

## Verification

- Unit tests above. Existing `ProcessingTests`, `ModelSafetyTests`, `ManagementTests` and `ConfigurationTests.VisibilityOwnsDatesAndRevisions` must pass unchanged, except the one noted in step 3.
- Private instance check: after upgrade, the count of "older policy" cards is unchanged, and a threshold change from 4 to 5 hides the visible posts with a score of 4 (4 posts on 2026-11-09) in one Manage save with zero model requests in the log. The 78 shielded posts of the always-show author stay visible. A change back to 4 shows the 4 posts again.

## Open questions for the reviewer

1. Should owner unhide become a durable marker that `Decide` respects? Owner answer (2026-11-09): yes. See target design item 6.
2. Should `thumbs` leave `HideOwners`? Rule 11 says thumbs never filter, and the code already forbids the value. Review answer (2026-11-09): no, not in this plan. `SetHidden` already rejects the value, so the removal changes nothing here.
3. Removed in review (2026-11-09) together with raw category storage. See "Out of scope".
4. `Reapply` over all posts inside a web request: acceptable? Owner answer (2026-11-09): yes when the locks are free, else the scheduler runs it (target design item 5). Step 3 measures the run time first.

## Out of scope

Prompt content, judge quality measurement (plan 09), video context repair (plan 10), the fairness and rotation machinery in `Processing.Run`.

Decided against in review (2026-11-09):

- Raw category storage with read-time `Restrict`. Category views select in SQL through `json_each(p.CategoriesJson)` (FeedQuery.cs L65-68), so a read-time rule cannot apply there. The prompt also tells the model to omit a close-friends-only category for other authors (Prompts.cs L23), so raw storage has nothing to restore after a close-friend add.
- Removal of close friends and `video_context` from the version, and a per-author rescore queue. Both are prompt inputs. Without them in the version, a hand edit of the config file leaves old verdicts marked as current.

## Notes from implement

Implemented on 2026-11-10 on branch `02-derived-visibility`. Differences from the text above:

- `Visibility.Decide(post, rules)` takes no separate config. `RuleContext` already carries the instance snapshot.
- The application operation is `VisibilityStore.Reapply`. A second class named `Visibility` in the Application namespace clashes with the Domain one in every file that imports both. The name follows the existing `Identity` and `IdentityStore` pair.
- Verdict apply (Processing.cs) uses `Visibility.Model`, the model's part of `Decide`, and not the full function. The full function re-evaluated the deterministic gates at every verdict. Six existing tests failed, because a verdict gave a `whitelist` hide to a visible post. It also undid an owner unhide of a muted post at the next processing run. `FreshVerdictDoesNotUndoAnOwnerUnhideOfAMutedPost` guards this.
- Step 3 measurement on a copy of the live database (10,856 posts, Debug build): `refilter --all` took about 14 seconds net. The cost was the entry resolution in `RuleContext.Shield` and `CloseFriend`. `ResolvedRules` now resolves the entries once per `Reapply` call, and the run takes about 2 seconds net.
- The same copy showed no visibility change from the new engine at the unchanged threshold. A threshold change from 4 to 5 hid the 4 posts with a score of 4, the 78 shielded posts stayed visible, and the change back showed the 4 posts again. The restamp moved 1,822 verdicts to the new version, and the number of distinct versions stayed 12.
- The restamp is `Prompts.RestampVersions`, called from CLI and Web startup.
- The CLI `refilter` summary line no longer says "newest first". `Reapply` works in id order, and the order does not change the result.
- The threshold field hint in Manage said "history needs explicit rescoring". It now says that a change applies to stored scores at once.

Not checked: the Manage threshold save on the real live instance. The unit tests cover the save path, and the copy covers the engine on live data.
