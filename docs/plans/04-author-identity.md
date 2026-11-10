# Plan 04: Author identity with allowlisted URL shapes, id-only merges and a merge log

Status: reviewed 2026-11-10 (`.work/04-author-identity/plan-review.md`). Scope reduced: no key kind column, no unmerge. Ready to implement.

## Outcome

Two authors merge only when the platform's own numeric id says they are one account. A URL shape the parser has not seen produces no key, instead of a shared key. Every merge and every alias conflict is recorded in the database and shown to the operator.

## Problem and evidence

`Identity.UrlRef` (`src/Feed.Core/Domain/Identity.cs` L10-20) takes the first path segment of any Facebook or Instagram URL as a profile key unless it is in `NonProfiles`, a 57-word blocklist (L9). `IdentityStore.Resolve` (`src/Feed.Core/Application/IdentityStore.cs` L10-58) treats any shared key as proof of one person and merges: posts move with `ContentRevision++` and `VisibilityRevision++` (L23), feedback, requests, visits and keys move (L24-27), sweep targets merge (L29-35), `IsFriend` is OR-ed and names coalesced (L36), the duplicate author row is deleted (L37). Nothing is logged.

Key strings do not say what they are. `fb:123` and `fb:johndoe` have the same form; `Refs` (L21-27) mixes id and slug keys into one list.

Known open bug (code review 2026-10-23, item 5): `/people/<name>/<id>` URLs yield the key `fb:people` for everyone. The live instance has no such URL yet. Any other new path shape would do the same.

Live instance, 2026-11-10 (read-only scan of a copy):

| Measure | Facebook | Instagram |
|---|---|---|
| Authors | 1,193 | 1,169 |
| Authors with `PlatformAuthorId` null or non-numeric | 0 | 0 |
| Numeric id keys | 1,193 | 1,169 |
| Slug or handle keys | 1,087 | 1,171 |
| Authors with two or more numeric keys | 0 | 0 |
| Authors with two or more alias keys | 1 | 2 |
| Observed author URLs: single-segment slug or handle | 3,655 | 6,960 |
| Observed author URLs: `profile.php?id=N` | 240 | 0 |
| Observed author URLs: `/groups/N/<word>/N` (no key today, `groups` is blocked) | 1 | 0 |
| Observed author URLs: `/people/<name>/<id>` or `/profile/<id>` | 0 | 0 |

Every author has a numeric id. Slugs are aliases, never the only evidence. No author holds two numeric keys, so no past merge joined two different platform ids; the id-only rule would have produced the same authors as today. The three authors with two alias keys are handle or slug renames joined through the id.

## Current behavior to keep in mind

- `AuthorKey(Key, Platform, AuthorId)` with `Key` as the primary key, so one key has exactly one owner; cascade delete from Author (`FeedDb.cs` L15). Authors have a unique index on `(Platform, PlatformAuthorId)`.
- `Identity.Matches` and `Resolve` (L40-52) match preference entries: with a colon, exact key; without, the full key tail after the prefix (`k[(k.IndexOf(':') + 1)..] == query`) or the display name. Verified 2026-11-10: `123` cannot match `fb:45123` today. `Management.Reference` (L43) writes `prefix:PlatformAuthorId` for new entries.
- Preference formats: `- mute: <name or ref>`, `- show: <name or ref>`, `close_friends` as handles or display names. Refs are `fb:<handle or id>` or `ig:<handle>` (`docs/04-instance.md` 4.4).
- Callers of `UrlRef`: `Capture.cs` L184 (`--person`), `Actions.cs` L10 and `Scheduler.cs` L48 (person URL validation), `Site.cs` L91 (timeline ownership), `People.cs` L17 (profile URL for a person collect), `PayloadParser.cs` L551 (Facebook post author id falls back to the URL key when the actor carries no id) and L559-561 (friends import: the URL key is the person id when it is numeric, else `ent_id`, else the slug itself).
- Callers of `IdentityStore.Resolve`: `Ingest.cs` L58 (friends import, `friend: true`) and L111 (post ingest). Both platforms and the friends import go through this one method.
- `FriendsCompleteness.Prune` (IdentityStore L76-81) unfriends by key absence, only with proven completeness.

## Target design

1. **Typed refs, computed per observation.** `Identity.Refs` returns `Ref(Key, Id)` records instead of strings. The ref built from `person.Id` is an id ref only when the id is all digits. Every other ref is an alias: a non-numeric `person.Id` (the parser's slug and handle fallbacks at `PayloadParser.cs` L444, L551, L558, L561) and every key from a URL. The kind is a property of the observation, never of the key string, so `fb:123` from `profile.php?id=123` is an alias ref and the same string from `person.Id` is the id ref. No column, no migration, no backfill: `AuthorKeys` stays `(Key, Platform, AuthorId)`.
2. **Allowlisted URL shapes.** `UrlRef` recognizes, for Facebook: `profile.php?id=N` gives `fb:N`; `/people/<anything>/<N>` gives `fb:N`; a single segment not in `NonProfiles` gives `fb:<segment>` (a numeric segment gives `fb:N`, as spec 5.6 says); a two-segment path whose second segment is a known profile tab (`about`, `photos`, `videos`, `reels`, `friends`, `friends_mutual`, `friends_all`, `posts`, `tagged`, `followers`, `following`, `saved`, `map`) gives the first segment as a slug. For Instagram: a single segment not in `NonProfiles` gives `ig:<handle>`, and a handle followed by a tab (`reels`, `tagged`, `saved`, `followers`, `following`) gives the handle. Every other shape returns null. `NonProfiles` stays as the safety net for the first segment.
3. **Id-only merges.** `Resolve` collects candidates as: the owner of the observation's id key, plus the owners of its alias keys that have no numeric `PlatformAuthorId` of their own (rows created from an observation with no id or with a slug fallback; they carry no id evidence that could disagree). Two or more candidates merge as today, except that the row with a numeric id is kept first (then friend flag, avatar, post count, lowest id). When the observation's alias is owned by an author whose numeric id differs from the observation's id, the alias stays where it is and a conflict event is written (item 4); the observation's author is the id owner, or a new row. An observation with no id attaches to the single owner of its alias, or creates a new row with `PlatformAuthorId = person.Id` (null or the parser's fallback, as today). When the resolved author has no numeric `PlatformAuthorId` and the observation has one, `Resolve` sets it: a numeric id is more evidence, not a missing value.
4. **Merge and conflict log.** New table `IdentityEvents(Id, Platform, Kind, Key, ObservedId, KeptAuthorId, RemovedAuthorId, DetailJson, At, LastAt, Count)`. `Kind` is `merge` or `alias_conflict`. A merge row holds the removed author's id and, in `DetailJson`, its columns, its keys and the moved post ids: enough for a repair by hand. A conflict row holds the alias key, the observed id, the keeping author and, in `DetailJson`, the observed name and URL. Conflict rows are one per `(Platform, Key, ObservedId)`: a repeat bumps `Count` and `LastAt`, so one reused handle cannot fill the log.
5. **No undo.** Merges now join only an id-bearing row with id-less rows, so a merge is an adoption, not a guess. The log gives the facts for a repair by hand. `identity unmerge` is out of scope (plan review 2026-11-10).
6. **Preference matching stays as it is.** `Matches` without a colon already compares the full key tail after the prefix, never an arbitrary suffix (verified above). This item is a test only: pin the rule with a test for the exact tail, the display name and the false-suffix case.
7. **Operator visibility.** `status` prints one `WARNING identity:` line per alias conflict: the key, the keeping author's name and id, the observed id and name, the count and the last time. Manage People shows the last 20 identity events (time, kind, platform, key, the kept person as a link, the removed or observed name from `DetailJson`, the count). No undo link.

## Contracts to preserve

- Binding rule 5: identity resolved at insert, keys namespaced per platform, no sync pass after insert. Post ingest on both platforms and the friends import call the one `IdentityStore.Resolve`. The log is append-only.
- Merges still invalidate moved posts' model state and re-apply filters (IdentityStore L45-56).
- Friend import stays add-only. `Prune` logic is unchanged.
- Preference entries written by Manage keep the `prefix:id` form. Existing hand-written entries by handle keep working through alias keys.

## Implementation sequence

1. Fix the open bug first, on its own: add `people` handling to `UrlRef` so `/people/<name>/<id>` yields `fb:<id>`. Test with both URL forms. This can ship before the rest.
2. Migration: the `IdentityEvents` table.
3. `UrlRef` allowlist and typed `Refs`. Tests: each accepted shape, each rejected shape (`/groups/x`, `/share/x`, `/photo`, `/people/<name>` with no id, a two-segment path with an unknown second segment), reserved single segments, the numeric single segment, a handle with a tab, a non-numeric `person.Id` typed as an alias.
4. `Resolve` id-only merge, adoption and alias conflict. Tests: two authors with different numeric ids that share only an alias do not merge and a conflict event is written once per observed id (a repeat bumps `Count`); an id-less author that owns the alias is adopted and gets the id; an id-less owner merges into the id owner with a merge event that lists the moved posts and keeps the id-bearing row; an observation with an alias only attaches to its single owner. `MergeInvalidatesMovedPostContentAndPreservesFeedbackAndCoverage` and `IdentityMergesAliasesWithoutMergingUnknowns` keep passing (their duplicate rows are id-less).
5. `status` lines and the Manage People list.
6. Tests for `Matches`: exact tail, display name and the false-suffix case. No code change.

## Spec updates

- `docs/05-data-model.md` 5.6 Identity L362-396 (typed refs, id-only merge, adoption, conflicts, events table), a new `IdentityEvents` table section.
- `docs/06-collectors.md` L154, L189 (author id sources unchanged; URL shapes listed).
- `docs/04-instance.md` 4.4 refs (unchanged form; note that handles are aliases).
- `docs/13-cli.md`: the `status` identity warning line.
- `docs/15-ui.md`: Manage People identity events.
- `docs/02-overview.md` rule 5: add "merges require a shared platform id".

## Verification

- Fast suite plus the tests above. `StorageTests.MergeInvalidatesMovedPostContentAndPreservesFeedbackAndCoverage` must still pass.
- Friends import before and after the allowlist, on a copy of the instance (config, database and the stored `friends-*` raw folders only): `friends --platform X --offline --all-dirs` with the main build and with the branch build. The found counts and the author counts must be equal. A read-only scan of the stored Facebook friends raws (2026-11-10) found 393 single-segment profile links, 41 `profile.php?id=N` links, 366 `/<slug>/friends_mutual` links and 18 other two-segment links; the tab allowlist covers `friends_mutual`.
- Live impact, measured 2026-11-10 on a copy: no author on either platform holds two numeric keys, so the id-only rule would have produced the same authors as today; zero past merges joined two platform ids.

## Decisions from the plan review (2026-11-10)

1. URL shapes: the private captures hold only single-segment slugs and handles, `profile.php?id=N` and one `/groups/...` URL. `/people/` and `/profile/` do not occur yet. `/profile/<N>` is not added without an observation. Profile tab paths are accepted because the stored friends raws carry them.
2. An alias in conflict stays with its first owner. The `status` line names both sides so the owner can rewrite a handle entry as `ig:<id>` or `fb:<id>`.
3. `IsFriend` stays OR-ed on a merge; both rows are the same account.
4. No undo. The log holds the facts for a repair by hand.

## Out of scope

Friends import completeness, person pages, whitelist semantics. A stored key kind column (the kind is computed per observation). `identity unmerge` and a Manage undo link (plan review 2026-11-10: merges now only adopt id-less rows, so an undo has no realistic use; the log holds the facts for a repair by hand).
