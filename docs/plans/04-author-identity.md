# Plan 04: Author identity with allowlisted URL shapes, id-only merges and a merge log

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

Two authors merge only when the platform's own numeric id says they are one account. A URL shape the parser has not seen produces no key, instead of a shared key. Every merge is recorded and can be undone.

## Problem and evidence

`Identity.UrlRef` (`src/Feed.Core/Domain/Identity.cs` L10-20) takes the first path segment of any Facebook or Instagram URL as a profile key unless it is in `NonProfiles`, a 57-word blocklist (L9). `IdentityStore.Resolve` (`src/Feed.Core/Application/IdentityStore.cs` L10-58) treats any shared key as proof of one person and merges: posts move with `ContentRevision++` and `VisibilityRevision++` (L23), feedback, requests, visits and keys move (L24-27), sweep targets merge (L29-35), `IsFriend` is OR-ed and names coalesced (L36), the duplicate author row is deleted (L37). Nothing is logged.

Key strings do not say what they are. `fb:123` and `fb:johndoe` have the same form; `Refs` (L21-27) mixes id and slug keys into one list.

Known open bug (code review 2026-10-23, item 5): `/people/<name>/<id>` URLs yield the key `fb:people` for everyone. The live instance has no such URL yet. Any other new path shape would do the same.

Live instance, 2026-11-09:

| Measure | Facebook | Instagram |
|---|---|---|
| Authors | 1,191 | 1,169 |
| Authors with `PlatformAuthorId` null | 0 | 0 |
| Numeric id keys | 1,191 | 1,169 |
| Slug or handle keys | 1,085 | 1,171 |
| Posts whose observed author URL is `profile.php?id=` | 239 | |

Every author has a numeric id. Slugs are aliases, never the only evidence.

## Current behavior to keep in mind

- `AuthorKey(Key, Platform, AuthorId)` with `Key` as the primary key, cascade delete from Author (`FeedDb.cs` L16). Authors have a unique index on `(Platform, PlatformAuthorId)`.
- `Identity.Matches` and `Resolve` (L40-52) match preference entries: with a colon, exact key; without, key suffix or display name. `Management.Reference` (L45) writes `prefix:PlatformAuthorId` for new entries.
- Preference formats: `- mute: <name or ref>`, `- show: <name or ref>`, `close_friends` as handles or display names. Refs are `fb:<handle or id>` or `ig:<handle>` (`docs/04-instance.md` 4.4).
- Callers of `UrlRef`: `Capture.cs` L21 and L180-182 (`--person`), `Actions.cs` L10 and `Scheduler.cs` L48 (person URL validation), `Site.cs` L89-91 (timeline ownership).
- `FriendsCompleteness.Prune` (IdentityStore L76-81) unfriends by key absence, only with proven completeness.

## Target design

1. **Typed keys.** Add `Kind` to `AuthorKey`: `id` or `alias`. `Refs` returns typed refs. Key strings stay unchanged so preferences and `Management.Reference` keep working.
2. **Allowlisted URL shapes.** `UrlRef` recognizes: Facebook `profile.php?id=N` (id), `/people/<anything>/<N>` (id), `/profile/<N>`? only if observed (open question 1), a single-segment slug not in `NonProfiles` (alias); Instagram a single-segment handle not in the reserved list (alias). Every other shape returns null. `NonProfiles` stays as a safety net for single segments.
3. **Id-only merges.** `Resolve` collects candidates by id keys only. If the observation has no id, it attaches to the single author that owns the alias, or creates a new author with the alias; it never merges two authors through an alias. When an alias key is already owned by an author with a different id than the observation, the alias stays where it is and a conflict row is written (see item 4). The observation's author is resolved by id.
4. **Merge and conflict log.** New table `IdentityEvents(Id, Platform, Kind, KeptAuthorId, RemovedAuthorId, SharedKeys, MovedPostIds, RemovedAuthorJson, At, RunId)`. `Kind` is `merge` or `alias_conflict`. The JSON snapshot holds the removed author's columns and keys.
5. **Undo.** CLI `identity unmerge --event N` recreates the removed author from the snapshot, moves `MovedPostIds` back, re-points keys listed in `SharedKeys` that belonged to it, bumps `ContentRevision` and `VisibilityRevision` on moved posts (model state invalid, same as a merge), and writes an `unmerge` event. `IsFriend` is restored from the snapshot for the recreated author and left unchanged on the kept one.
6. **Tighter preference matching.** `Matches` without a colon matches the full key tail after the prefix (`fb:` + entry), never an arbitrary suffix. Verify first whether the current suffix rule can match `123` against `fb:45123`; if it can, this is a correctness fix.
7. **Operator visibility.** Manage People shows the last 20 identity events with an undo link that runs the CLI command through the request queue. `status` prints a line when alias conflicts exist.

## Contracts to preserve

- Binding rule 5: identity resolved at insert, keys namespaced per platform, no sync pass after insert. The log is append-only and the undo is explicit.
- Merges still invalidate moved posts' model state and re-apply filters (IdentityStore L45-56).
- Friend import stays add-only. `Prune` logic is unchanged.
- Preference entries written by Manage keep the `prefix:id` form. Existing hand-written entries by handle keep working through alias keys.

## Implementation sequence

1. Fix the open bug first, on its own: add `people` and `profile` handling to `UrlRef` so `/people/<name>/<id>` yields the id key. Test with both URL forms. This can ship before the rest.
2. Migration: `AuthorKeys.Kind` with a backfill (numeric key equal to the owner's `PlatformAuthorId` is `id`, else `alias`); `IdentityEvents` table.
3. `UrlRef` allowlist and typed `Refs`. Tests: each accepted shape, each rejected shape (`/groups/x`, `/share/x`, `/photo`, two-segment paths), reserved single segments.
4. `Resolve` id-only merge and alias conflict. Tests: two authors sharing only an alias do not merge and a conflict event is written; two authors sharing an id merge with a merge event that lists moved posts; an observation with an alias only attaches to its single owner.
5. `identity unmerge` and the Manage list. Test: merge then unmerge restores both authors, post counts and friend flags, and bumps revisions on moved posts.
6. `Matches` tightening with tests for exact tail, display name and the false-suffix case.

## Spec updates

- `docs/05-data-model.md` 5.6 Identity L362-396 (typed keys, id-only merge, events table, undo), AuthorKeys L38.
- `docs/06-collectors.md` L154, L189 (author id sources unchanged; URL shapes listed).
- `docs/04-instance.md` 4.4 refs (unchanged form; note that handles are aliases).
- `docs/08-filters.md` 8.5, 8.6 (matching rule).
- `docs/13-cli.md`: `identity unmerge`.
- `docs/15-ui.md`: Manage People identity events.
- `docs/02-overview.md` rule 5: add "merges require a shared platform id".

## Verification

- Fast suite plus the tests above. `StorageTests.MergeInvalidatesMovedPostContentAndPreservesFeedbackAndCoverage` must still pass.
- Private instance: after the migration, count alias keys whose owner's id differs from the id in the same observation history. Expected zero conflicts today; any found are real past mis-merges to inspect.

## Open questions for the reviewer

1. Which Facebook URL shapes appear in the private captures beyond `profile.php`, slugs and `/people/`? A one-off read-only scan of `ObservedAuthorUrl` decides the allowlist.
2. When an alias conflict occurs, should the alias move to the newest observation's author, or stay with the first owner as proposed?
3. Should `IsFriend` OR remain for id merges? Both rows are the same account, so yes is proposed.
4. Undo through Manage, or CLI only?

## Out of scope

Friends import completeness, person pages, whitelist semantics.
