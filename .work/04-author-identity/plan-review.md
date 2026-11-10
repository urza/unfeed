# Round 1 (2026-11-10)

Target: `docs/plans/04-author-identity.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: `Resolve` runs once per ingested post and per imported friend; the events table grows), ux (reason: a `status` line, Manage People and the URL shapes the operator pastes). Security was left out: the only input is the profile URL that the parser already handles, and the app has one owner.

Before the review the session corrected the plan's anchors against the code (`Capture.cs`, `Management.Reference`, the missing `UrlRef` callers in `PayloadParser.cs` and `People.cs`, the `Matches` claim) and added the read-only scan of a copy of the live database. The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The real risk is a merge through a shared handle or slug. Today's code does this silently, and a reclaimed Instagram handle can trigger it. The id-only rule closes that risk at low cost: no migration on AuthorKeys, one small table, and the merge result matches today's authors (scan). The /people/ fix ships first and stands alone.

The step-back agent named one optional part, the Manage People events list: merges join id-less rows, the instance has none, so the list will almost always be empty, and the `status` line already covers the "not silent" request. It did not count this against the plan.

User (owner proxy, 2026-11-10): GO. The Manage People list stays, but it renders only when events exist, so an empty instance shows nothing new. The owner reads Manage more often than `status`, and the list is about ten lines of markup over the same query.

## Skeptic's cons

1. **Most of the plan guards against an event that cannot happen.** (Target design 4, 5 and 7, and the scan table.) Every author has a numeric id, `(Platform, PlatformAuthorId)` is unique, and no author holds two numeric keys. An id-only merge therefore needs a stray extra id key, and the scan found none. Price: a migration, a new table, a CLI command and a Manage UI to maintain for dead paths. Step 1 alone fixes the only known bug.
   Answer: Agree in part. The kind column, the unmerge command and the undo link are gone (findings 4 and 5). Disagree that step 1 is enough: today's code merges two authors through any shared slug or handle (`IdentityStore.cs` L13-19), and an Instagram handle can be released and reclaimed. The scan proves that this has not happened yet, not that it cannot. What stays is one table, one `status` line and one list.
   User (owner proxy, 2026-11-10): keep the id-only rule and the log; drop the kind column, the undo and the queue. The owner asked for merges that are not destructive and not silent; the log is the minimum for "not silent".

2. **The undo restores only part of a merge.** (Item 5.) A merge moves feedback, requests, visits, sweep targets and names; the unmerge moves back only `MovedPostIds` and some keys; posts ingested after the merge and chained merges are not covered. Price: an "undo" that leaves a half-split author.
   Answer: Agree. Item 5 is removed.
   User (owner proxy, 2026-11-10): removed. The log keeps the removed author's snapshot for a repair by hand.

3. **Alias-only observations can create authors that never heal.** (Item 3, "creates a new author with the alias".) A later observation with the id finds no id match, so it creates a second author, and the alias raises a conflict instead of a merge. Price: permanent duplicates.
   Answer: Agree. Item 3 now adopts an id-less alias owner (sets its `PlatformAuthorId`) or merges it into the id owner. Only an owner with a different numeric id is a conflict.
   User (owner proxy, 2026-11-10): adopted as written in item 3.

4. **The allowlist can drop post authors.** (Callers, `PayloadParser.cs` L551.) That line falls back to the URL key when the actor has no id; each unlisted shape now returns null. Price: authorless posts with no event logged.
   Answer: Disagree. Today an unlisted shape gives a wrong shared key (`/people/<name>/<id>` gives `fb:people` for every such author), which is the bug this plan fixes. An authorless post is the behavior spec 5.6 prescribes for an observation with no proven ref, and `/people/` now yields the id. The live captures hold no other multi-segment author URL (scan table).
   User (owner proxy, 2026-11-10): no change.

5. **"Alias stays with the first owner" goes stale when a handle is reused.** (Item 3 and open question 2.) A `mute:` or `close_friends` entry by handle then targets the old account; the only signal is one `status` line. Price: filters that act on the wrong person with no warning.
   Answer: Agree with the price, disagree with the remedy. Moving the alias is the destructive step the plan removes; it would also move every handle entry to the new account silently. The `status` line now names the key, both authors and the count, so the owner can rewrite the entry as `ig:<id>`.
   User (owner proxy, 2026-11-10): keep the first owner; the status line names both sides (decision 2 in the plan).

6. **The open questions are already decided in the design.** (Open questions 2 to 4 against items 3, 5 and 7.)
   Answer: Agree. The open questions are now a "Decisions" section.
   User (owner proxy, 2026-11-10): done.

7. **The verification depends on an "observation history" the plan never defines.** (Verification, second bullet.)
   Answer: Agree. The bullet is replaced by the scan result: no author holds two numeric keys, so no past merge joined two platform ids.
   User (owner proxy, 2026-11-10): done.

## Findings

### 1. Under the id-only rule a merge can only fire on contradictory evidence: Major, FIX
Reviewers: neutral, minimalist, ux. Where: Target design items 2 to 5; `FeedDb.cs` L15 (`Key` is the primary key)
Finding: One key has one owner, so an observation can name two id owners only when `person.Id` and a `profile.php?id=` or `/people/` URL carry different numbers. That merge joins two accounts, which is the wrong merge the plan wants to stop, and after an unmerge the next collect redoes it.
Resolution: Only the ref built from `person.Id` is the id ref; every URL key is an alias (item 1). Two authors with different numeric ids never merge. A disagreement between the id and an alias owner is a conflict row.

### 2. Slug and handle fallbacks in `Person.Id` would become id keys: Major, FIX
Reviewers: neutral. Where: Target design item 1; `PayloadParser.cs` L444, L551, L558, L561
Finding: Four parser paths put a slug or a handle into `Person.Id` when no numeric id exists. Typed by source, those become id keys, and two authors with the same slug merge "by id".
Resolution: The id ref requires an all-digit `person.Id`. A non-numeric id is an alias ref. The same rule decides whether an author row carries id evidence (`PlatformAuthorId` numeric).

### 3. An id-less author that owns an alias was not covered: Major, FIX
Reviewers: neutral, minimalist. Where: Target design item 3; `IdentityStore.cs` L42 (never sets `PlatformAuthorId` on an existing row)
Finding: An observation with no id creates a row with a null id and the alias. A later observation with the id and the same URL found no rule: "never merge through an alias" and "owner with a different id" both miss a null id.
Resolution: Item 3 now lists the id-less owner as a merge candidate (adoption when it is the only one, merge into the id owner otherwise) and sets `PlatformAuthorId` when the observation brings a numeric id. Step 4 adds both tests.

### 4. The `AuthorKeys.Kind` column and its backfill are not needed: Major, FIX
Reviewers: minimalist, neutral. Where: Target design item 1; sequence step 2
Finding: `Resolve` knows which ref came from `person.Id` and which from the URL. A stored copy of that fact can drift from the runtime rule (the backfill rule and the runtime rule in the plan already differed).
Resolution: No column, no migration for `AuthorKeys`. `Refs` returns `Ref(Key, Id)` records computed per observation.

### 5. The merge log, the undo, the Manage list and the status line serve a path that rarely runs: Major, ESCALATE
Reviewers: minimalist, ux, neutral. Where: Target design items 4, 5 and 7; open question 4
Finding: With id-only matching the merge loop runs only for id-less rows, of which the live instance has zero. The minimalist wants one log line and nothing else. The ux reviewer adds that an undo through the request queue waits for a scheduler tick (the queue runs collect, friends, login, like and process only, spec 12.6) and that a Manage row with two author ids does not tell the operator who was merged.
Resolution: Question for the owner. Recommended: drop the undo (CLI and link) and keep a small durable log in the database, a `status` line per conflict and a Manage People list that shows names from the event snapshot.
User (owner proxy, 2026-11-10): yes. The owner's stated minimum is id-only merges, logged alias conflicts and a durable merge log; undo was optional. The log is one table with no second write path.

### 6. A repeated conflict fills the event list: Major, FIX
Reviewers: ux, performance (Nit). Where: Target design items 3, 4 and 7
Finding: One conflict row per observation. A reclaimed handle writes a row for every post of the new owner in every run, and the merge rows fall out of the "last 20".
Resolution: Conflict rows are one per `(Platform, Key, ObservedId)` with `Count` and `LastAt` (item 4).

### 7. Unrecognized friend URL shapes drop the friend, and the prune can then unfriend: Major, FIX
Reviewers: ux. Where: Target design item 2; `PayloadParser.cs` L559; `IdentityStore.cs` L76-81
Finding: `PersonFrom` returns null when `UrlRef` returns null. A friend whose link has a tab path (`/<slug>/about`) would vanish from the import, and a proven-complete import would then unfriend them with no message.
Resolution: A read-only scan of the stored Facebook friends raws found 393 single-segment links, 41 `profile.php?id=N`, 366 `/<slug>/friends_mutual` and 18 other two-segment links. Item 2 accepts a known profile tab as the second segment (`about`, `photos`, `videos`, `reels`, `friends`, `friends_mutual`, `friends_all`, `posts`, `tagged`, `followers`, `following`, `saved`, `map`; Instagram `reels`, `tagged`, `saved`, `followers`, `following`). Verification adds an offline friends import before and after the change on a copy; the counts must be equal.

### 8. A pasted profile URL with a tab path would be refused: Nit, FIX
Reviewers: ux. Where: Target design item 2; `Actions.cs` L10
Finding: `collect --person https://www.instagram.com/<handle>/reels/` works today and would fail with "Invalid person collection target".
Resolution: Covered by the tab allowlist in finding 7. The error message now names the accepted shapes.

### 9. An event row must name the people: Nit, FIX
Reviewers: ux. Where: Target design item 7
Finding: The removed author no longer exists, so a row with two ids does not tell the operator who was merged. A conflict row cannot be undone, but every row had an undo link.
Resolution: Item 4 stores the removed author's columns and the observed name in `DetailJson`; item 7 shows them. No undo link.

### 10. The `status` line must say what to do: Nit, FIX
Reviewers: ux. Where: Target design item 7
Finding: "Conflicts exist" does not tell the owner which handle now points at the wrong person.
Resolution: One line per conflict with the key, both names and ids, the count and the last time; the owner rewrites the entry as `ig:<id>` or `fb:<id>`.

### 11. The numeric single segment and a wrong line number: Nit, FIX
Reviewers: neutral. Where: Target design item 2; spec 5.6 L364; "Current behavior"
Finding: Spec 5.6 says a numeric first segment gives `fb:N`; the allowlist typed every single segment as an alias. `AuthorKey` is configured at `FeedDb.cs` L15, not L16.
Resolution: Item 2 keeps `fb:N` for a numeric segment; the kind is a property of the ref, not of the string, so the spec line stays true. The line number is corrected.

### 12. The post-migration conflict count is already answered: Nit, FIX
Reviewers: minimalist. Where: Verification, second bullet
Finding: The scan shows no author with two numeric keys.
Resolution: The bullet now records the scan result instead of a check to run.

### 13. Performance: All good
Reviewers: performance. Checked: the id lookup stays a primary-key read per post; the events table is small with the dedupe; the merge path runs no more often than today; no cache, queue or batch is needed.
