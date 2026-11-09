# Round 1 (2026-11-09)

Target: `docs/plans/02-derived-visibility.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: `Reapply` over all posts in a web request, a startup restamp), ux (reason: the plan changes what Manage saves and CLI `refilter` do for the owner)

## Step back
Verdict: CHANGE COURSE

Why: The real problems are threshold edits that never reach history and two refilter paths that disagree. Steps 1 to 3 fix the refilter split, so keep them. The hash split goes too far. Close friends and video context are real prompt inputs (`author_close_friend`, Prompts.cs L71), so taking them out of the hash causes skeptic con 1 and requires a rescore queue that does not exist (escalation 10).

Instead: Take only the threshold out of `ConfigurationHash`, and replace the full bypass list with the `bypassLlm` flag. Then do the one-time restamp. Keep close friends and video context in the hash. Remove step 6 and item 6. For the threshold save, set `management:refilter_pending` and reuse the `ResumeRefilter` path that exists today (Management.cs L150, L256). This makes escalation 11 small. Settle escalation 12 (`OwnerUnhiddenAt`) before step 5 ships.

User (2026-11-09): accepted. Only the threshold leaves the version; close friends and video context stay. The plan now has no rescore queue and no raw category storage.

## Skeptic's cons

1. **Edits to the config file no longer mark anything stale.** (Target design 3 and 4.) Only a Person save or a preferences always-show edit in Manage queues a rescore. A YAML edit to `close_friends`, always-show or `video_context` changes the prompt input and leaves every verdict "current". The price: the owner sees verdicts built on old prompt facts, and no trail line tells them so.
   Answer: Agree: a hand edit of `close_friends` in `config.json` gets no version change and no trigger. The plan must state this loss or add a check at snapshot load. Always-show edits are not affected: they stay in the preferences hash (`Configuration.cs` L132).
   User: Resolved by the smaller course. Close friends and `video_context` stay in the version.

2. **A threshold save will now re-hide posts the owner released.** (Target design 5, Contracts "Owner unhide", Open question 1.) `Reapply(all)` turns every threshold tweak into a full refilter, and owner unhide is not durable. The price: posts the owner unhid come back hidden more often. Open question 1 must be settled before this ships, and the plan leaves it open.
   Answer: Agree: this is escalation 3 below. Spec 8.9 already permits a refilter to hide the post again, but step 5 adds a new trigger.
   User: Add the durable unhide marker (finding 12).

3. **"Identical by construction" is false.** (Sequence step 2, Verification.) `Decide` computes the result from scratch. `Apply` today never lets a gate clear a deterministic hide. When a keyword or mute is gone, `Decide` clears that hide. The price: the "must pass unchanged" tests fail or hide a change in behavior.
   Answer: Agree with the claim, disagree with the example. `Filters.Apply` clears a deterministic hide today when no gate matches (`Identity.cs` L76). Three other results do change; finding 2 lists them and step 2 now states them. The plan sentence "no gate clears a deterministic hide" caused the wrong reading and needs a reword.
   User: Step 2 lists the three changes; the ambiguous sentence in "Current behavior" is reworded.

4. **The 78-post check measures the wrong set.** (Verification.) Those 78 posts are below the current threshold and still visible, so something already keeps them visible (a shield or an unhide). A move from 4 to 5 hides a different set. The price: the check cannot pass or fail in a way that means anything.
   Answer: Agree: the live database confirms it. The threshold is 4 now. 78 visible judged posts have a score below 4, and 4 more have a score of 4. A `Reapply` at the unchanged threshold hides the 78 (minus shielded authors). A move to 5 hides 82. The check must first classify why the 78 are visible.
   User: Corrected. All 78 posts belong to the one always-show author (author check on the live database, 2026-11-09), so the `llm` bypass shields them. The evidence row says so, and the check now uses the 4 visible posts with a score of 4.

5. **One plan carries three changes.** (Target design 1-6.) It unifies the visibility engine, splits the version and changes category storage. Item 6 is a schema change with an open question attached. The price: a reviewer cannot ship or roll back one change without the others.
   Answer: Agree for item 6: see escalation 4, which recommends its removal. Disagree for the other two: the threshold leaves the hash (step 4) only safely after `Reapply` exists (step 3), and the sequence already gives each step its own suite run.
   User: Item 6 is removed.

6. **The per-author rescore brings back the model cost.** (Target design 4.) One always-show name can match many authors. The price: model calls the plan says it removes.
   Answer: Agree in part: finding 1 removed always-show from item 4, so the trigger now covers one author per close-friend save. Escalation 1 asks if the trigger must exist at all.
   User: Step 6 is removed.

7. **`Reapply(all)` runs inside a web request.** (Target design 5, Open question 4.) It bumps `VisibilityRevision` while a processing run commits on the same column. The price: verdict commits that conflict, and a slow save.
   Answer: Agree: escalation 2 covers the locks, and finding 6 adds a measurement before step 5.
   User: See finding 11. `Reapply` runs only with the processing and ingest locks held.

## Findings

### 1. Always-show names and the `llm` bypass are global prompt input: Major, FIX
Reviewers: neutral, minimalist. Where: Target design 3 and 4; `Prompts.cs` L32; `Configuration.cs` L132
Finding: `Prompts.Policy` adds the full always-show list to every prompt, only when the bypasses contain `llm`. The plan removed the whole bypass list from the hash and treated always-show as a per-author fact. A flip of `llm` then changes every prompt with no version change.
Resolution: Item 3 now keeps `bypassLlm` in the hash. Item 4 and step 6 now cover close friends only. Always-show edits keep their global version change through the preferences hash.

### 2. Step 2 is not "identical by construction": Major, FIX
Reviewers: neutral, skeptic. Where: Target design 1; Implementation step 2; `Ingest.cs` L115; `Identity.cs` L73-78
Finding: A post with an `llm` hide whose content changes keeps the hide today. `Decide` makes it visible, because `Judged` is false. Two more results change (the `llm` hide returns on every path; an `llm` hide clears when the score is at or above the threshold).
Resolution: Step 2 now lists the three intended changes and asks for a test for each. The live database holds 0 posts in the first state today.

### 3. `Reapply` did not say when it increments `VisibilityRevision`: Major, FIX
Reviewers: neutral. Where: Target design 1 and 2; `Management.cs` L271; `Program.cs` L179; `Processing.cs` L238, L243
Finding: Both loops increment the revision on every post. Item 1 said "only when owner or reason changed". With that rule, an in-flight verdict saves categories restricted by the old close-friend list.
Resolution: Item 2 now keeps today's rule (increment every post in scope) and gives the reason.

### 4. `Reapply` had callers and scope kinds it does not need: Nit, FIX
Reviewers: minimalist. Where: Target design 2; Contracts, rule 13
Finding: Ingest, verdict apply and identity merge hold one loaded post in their own transaction. They need `Decide`, not a database operation. The plan also did not say that `Reapply` restricts categories.
Resolution: Item 2 now takes an `IQueryable<Post>` filter with three callers (Manage refilter, Person save, CLI refilter) and states the `Restrict` write. The rule 13 contract line now names `Decide`.

### 5. The restamp cannot run in `FeedDb.Initialize`, and it missed text-only versions: Nit, FIX
Reviewers: neutral, minimalist. Where: Implementation step 4; `FeedDb.cs` L47
Finding: `Initialize` has no snapshot, so it cannot compute the old version. Posts judged with `--text-only` carry `Version(s, true)`. A named `LegacyConfigurationHash` kept "for one release" adds a cleanup task.
Resolution: Step 4 now runs the restamp at CLI and Web startup after the snapshot loads, covers both modes, and keeps the old hash as a private helper inside the restamp.

### 6. `Reapply` over all posts repeats the identity lookup for each post: Major, FIX
Reviewers: performance. Where: Target design 1, 2, 5; `Identity.cs` L39, L47-58
Finding: `Shield` and `CloseFriend` resolve each entry against all authors for each post, and `Keys` parses `RefsJson` each time. The live instance has 10,815 posts, 2,360 authors, 1 always-show entry and 32 close-friend entries. The reviewer did not time it. Step 5 makes this run on each threshold save.
Resolution: Step 3 now measures the run on a copy of the live database, with the precomputed id sets as the remedy above 2 seconds.

### 7. The confirmation text after a threshold save becomes false: Nit, FIX
Reviewers: ux. Where: Target design 5; `Management.cs` L214
Finding: The `processing` save returns "New runs use these settings". After step 5, existing posts change at once.
Resolution: Step 5 now changes the text and returns the hidden and shown counts.

### 8. CLI `refilter` default scope differs from Manage: Nit, FIX
Reviewers: ux. Where: Outcome; Spec updates 13.2; `Program.cs` L178
Finding: A plain `refilter` covers posts since the last collect. Manage covers all history. A CLI user who unmutes and runs `refilter` leaves older posts unchanged.
Resolution: The 13.2 spec line now states the scope difference and the `--all` advice.

### 9. Open question 2 (`thumbs` in `HideOwners`): Nit, FIX
Reviewers: minimalist. Where: Open questions 2
Finding: `SetHidden` already rejects the value. The removal changes nothing in this plan.
Resolution: The plan now records the answer "no, not in this plan".

### 10. The per-author rescore "queue" does not exist: Major, ESCALATE
Reviewers: minimalist, ux, skeptic. Where: Target design 4; Implementation step 6; `Management.cs` L251; `Program.cs` L124
Finding: `rescore --author` is a CLI run and fails when the model is disabled. A queue is a new background job with no visible state. The minimalist says cut step 6 and keep the hand-rescore message. The ux reviewer says name the mechanism and show it in the runs list.
Resolution: Question for the user. Recommended: cut the queue. The close-friend save message gives the exact `rescore --author` command.
User: Cut. The smaller course keeps close friends in the version, so no queue is necessary.

### 11. Lock rules for the threshold save are not stated: Major, ESCALATE
Reviewers: neutral, ux, minimalist, skeptic. Where: Target design 5; `Management.cs` L146-149, L256-263; `Processing.cs` L244
Finding: Refilter saves take the processing and ingest locks and fail with "Processing is active". The neutral reviewer wants the `processing` save to take them. The ux reviewer says the owner then cannot change `enabled` or `timeout_seconds` during a long run. An active run also applies the old threshold to verdicts that land after the save.
Resolution: Question for the user. Recommended: the save always succeeds. When the threshold value changed, it sets `management:refilter_pending` and tries the locks. If a lock is busy, `ResumeRefilter` (Scheduler.cs L33) does the work later, and the message says so.
User: Yes (2026-11-09). The save always succeeds. A changed threshold sets the pending key and reapplies now when the locks are free, else through `ResumeRefilter` when the run ends.

### 12. Owner unhide reverts on a threshold save: Major, ESCALATE
Reviewers: ux, skeptic. Where: Target design 5; Open question 1; `Actions.cs` L34; spec 8.9
Finding: A post with score 3 that the owner unhid goes back to hidden on the next threshold save. Spec 8.9 permits this for a refilter today. Step 5 adds a common trigger.
Resolution: Question for the user. Recommended: add `OwnerUnhiddenAt` in this plan. `Decide` skips the `llm` hide while the marker is set. A content change or a manual hide clears the marker. Deterministic gates stay as today.
User: Yes (2026-11-09). The plan adds `OwnerUnhiddenRevision` (target design item 6, new step 5; the threshold save is now step 6). The marker stores the content revision, so it expires on a content change with no clear code.

### 13. Raw category storage (item 6) cannot work at read time and gives no stated benefit: Major, ESCALATE
Reviewers: performance, minimalist, skeptic. Where: Target design 6; Implementation step 7; Open question 3; `FeedQuery.cs` L65-68; `Prompts.cs` L23
Finding: Category views pick posts in SQL with `json_each(p.CategoriesJson)`, so read-time `Restrict` shows restricted posts or needs a full load. The stated benefit also fails: the prompt tells the model to omit a close-friends-only category when `author_close_friend` is false. Raw storage has nothing to restore after a close-friend add.
Resolution: Question for the user. Recommended: remove item 6, step 7 and open question 3 from this plan.
User: Removed, with the reason recorded under "Out of scope" in the plan.
