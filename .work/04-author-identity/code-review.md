# Round 1 (2026-11-10)

Target: `git diff 7f87994...HEAD` (branch `04-author-identity`), spec `docs/plans/04-author-identity.md`

Reviewers: neutral, minimalist, performance (reason: `Resolve` runs once per ingested post and gained queries; a new table), ux (reason: a `status` line, a Manage People table, an error message and the docs changed). Security was left out: the only input is the profile URL the parser already handled, Razor encodes the names it renders, and the app has one owner.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The allowlist fixes a real bug class. Under the old blocklist, any new URL shape (`fb:people`, `fb:photo.php`) gave one key to many people and merged them silently. The id-only rule costs almost nothing: the live data gives zero merges between two platform ids, and the friends replay on main and on the branch gives equal counts.

The step-back agent named the events table with its Manage list and `status` line as the weakest part: live conflicts are close to zero, and a log line would cost less. It kept it, because a log line goes away while the conflict tells the owner to rewrite a mute or close-friend entry, the table is bounded by the dedupe, and it is built and tested. The work is fine to merge as it is.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. The owner of a URL-derived numeric key joins an observation with that id: Major, ESCALATE
Reviewers: neutral. Where: `IdentityStore.cs` candidate line; plan item 1; spec 5.6 kinds bullet
Finding: Row A (id 456) owns `fb:123` from an earlier `profile.php?id=123` URL. An observation with actor id 123 names `fb:123` as its id key, so A is a candidate and the observation attaches to A. The plan text called `fb:123` from a URL an alias ref, and the Outcome says two rows join only through the platform's own id.
Resolution: Question for the owner. The strict reading leaves the id-123 observation with a key-less row (A keeps `fb:123`; `Management.Reference` throws for a row with no keys) or moves the key away from A. The recommended answer keeps the code: a numeric key comes only from a platform id or from the platform's own profile URL, and the one known case of two numbers for one person is a Facebook delegate page, which is one person. The live scan has zero such rows.
User (owner proxy, 2026-11-10): keep the code rule. The plan item 1, spec 5.6 and the comment at the line now state it. The second half of the finding (A and a row B with id 123 merge) cannot occur after finding 4: the row with id N always owns the key for N.

### 2. Two id-bearing alias owners with no id ref resolve to the lowest row id: Nit, DECLINE
Reviewers: neutral. Where: `IdentityStore.cs`, the `MinBy` fallback
Finding: An observation with a slug fallback owned by A and a URL handle owned by B would pick A with no basis and no event.
Resolution: Declined. Both parser fallbacks derive the slug from the same URL (`PayloadParser.cs` L551, L561; Instagram builds the URL from the handle), so an observation without an id carries one alias. The comment at the line says so.

### 3. `photo.php` and other `.php` pages were accepted as slugs: Nit, FIX
Reviewers: neutral. Where: `Identity.cs` `UrlRef`
Finding: `facebook.com/photo.php?fbid=1` gave `fb:photo.php` for every such URL, the shared-key bug class. Pre-existing, not a regression.
Resolution: A single segment that ends in `.php` yields no key (`profile.php` is handled before). Test added.

### 4. The extra `(Platform, PlatformAuthorId)` lookup in `Resolve` guards a state the code cannot reach: Nit, FIX
Reviewers: minimalist. Where: `IdentityStore.cs`
Finding: `Resolve` is the only writer of Authors; a row with id N is created only when nobody owns `prefix:N` and then gets that key, and an adopting row gets it the same way. The lookup ran one query per ingest with an id and never added a candidate.
Resolution: The line is deleted; the comment states the invariant that makes it unnecessary.

### 5. A merge row showed the kept person's id next to the removed person's name: Major, FIX
Reviewers: ux. Where: `Manage.razor` identity events table
Finding: `ObservedId` of a merge event is the kept row's id (or a slug fallback), but the "Other person" cell showed it as the removed person's id.
Resolution: `IdentityStore.Events` now gives the row an `OtherId`: the removed row's `PlatformAuthorId` from the snapshot for a merge, the observed id for a conflict.

### 6. The other person in a conflict row was not linked: Nit, FIX
Reviewers: ux. Where: `Manage.razor`
Finding: The guidance sends the operator to that person's details for the id ref, and the table showed only a name.
Resolution: A conflict row links the other person through the `AuthorId` in the event snapshot (`OtherAuthorId`).

### 7. Event times used a different format from the rest of Manage: Nit, FIX
Reviewers: ux. Where: `Manage.razor`
Resolution: The column uses the `At()` helper (exact time plus the relative label), as 15.14 asks.

### 8. Performance: All good
Reviewers: performance. Checked: the added queries are a fixed number of indexed point reads per observation; the events table is bounded by the conflict dedupe and the id-less row count; the Manage and `status` reads sort tens of rows; the migration rewrites nothing.
