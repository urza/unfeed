# Round 1 (2026-11-10)

Target: `git diff 2a2f481...HEAD` (branch `03-media-identity`), spec `docs/plans/03-media-identity.md`

Reviewers: neutral, minimalist, performance (reason: the normalization pass over 25,877 rows and the per-source key work), ux (reason: the rendition a card shows, and the docs changed). Security was left out: the only input is the CDN URL the parser already handled, and the app has one owner.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The fix targets the measured main cause of needless re-judges. The media key changed in 30 of 40 recapture pairs, and 27 of those 30 had the same filename set. The change is about 30 lines inside the existing key-version mechanism. It adds no column, no index and no parser bump, and the live-copy run kept sum(ContentRevision) at 85,249 while it removed 1,341 duplicate rows.

The step-back agent weighed each added mechanism against not having it: the widest-rendition pick in the Facebook walk (a small cost; without it about 100 cards keep a thumbnail), the `permalink:` guard (one line that stops a known orphan bug) and the lowest-slot rule (one line). It judged the decision to state the thumbnail limit correct: a re-fetch path would act on a signal the data shows is unreliable and would help 9% of the groups. Shared-link and tag churn stay out of scope correctly.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. The repair branch in `MediaKeys.Key` changed nothing a user sees: Nit, FIX
Reviewers: minimalist. Where: `MediaKeys.cs` (`Key`), `MediaTests.cs` (`NormalizationKeepsTheWiderRenditionAndPermalinkKeys`)
Finding: The plan asked for a guard that keeps a `permalink:` key. The diff added a branch that rebuilt the key for a video row whose URL equals the post permalink. On the live copy it repaired 3 of the 10 rows that version 2 damaged, and all 10 are retired rows, so no card or revision depends on them. For current rows the branch repeated the guard.
Resolution: The branch, the `Post` parameter and the repair case are gone. `Key(url, key)` keeps a `permalink:` key and otherwise computes the version 3 key. The test seeds the row with `permalink:102` and asserts that the key survives. The spec sentence about the repair is removed.

### 2. A stored thumbnail is never replaced by a wider rendition on a later capture: Major, ESCALATE
Reviewers: ux. Where: `Ingest.cs` L127 with the `file:` key; plan item 4 and decision 4
Finding: A first capture that lists a small rendition downloads it. A later capture that names the same asset at a larger size gives the same key, so ingest keeps the file on disk and fetches nothing. Under version 2 the different `stp` gave a new key, so the reader got the large picture at the price of a revision and a re-judge. The widest-rendition rule covers one payload and the one-time collapse, not the order across captures.
Resolution: Question for the owner. The ux reviewer offered two options: state the limit in 7.3, or fetch again when the stored width is small and the URL asks for a larger size. The session measured the copy: among same-asset groups with known sizes, the first download was the widest or equal in 607 of 708 Facebook groups; a later capture was wider in 80 Facebook and 21 Instagram groups of 1,142. The CDN also picks the served size by the signed parameters, so the URL's `stp` is not a reliable request for a larger file (520 of 523 host-rotated groups with the same `stp` differ in size). Recommended: state the limit.
User (owner proxy, 2026-11-10): state the limit. A re-fetch path would act on an unreliable signal, add a second file per slot and help 9% of the groups. 7.3 and the plan's contracts now say that a slot keeps the rendition downloaded first, with the numbers.

### 3. The collapse could change the image order on a card until the next recapture: Nit, FIX
Reviewers: neutral. Where: `MediaKeys.cs` (collapse)
Finding: With a manifest [x-thumb at 1, y at 2, x-wide at 3] the kept row x-wide stayed at position 3, so the card and the judge showed y before x while the manifest put x first. The next capture renumbers, but a post that is never captured again kept the changed order. In 100 of the 156 live two-rendition posts the kept row sat in the later slot.
Resolution: The kept row takes the lowest position of its group, with a comment at the line. The test puts the thumbnail at slot 1 with the newer id and asserts that the wide row wins and takes slot 1.

### 4. No test reached the `permalink:` guard, and no recapture asserted `Revised 0`: Nit, FIX in part
Reviewers: neutral. Where: `MediaTests.cs`; plan step 3
Finding: The test seeded a URL key, so only the repair branch ran. If someone deleted the guard, every test still passed.
Resolution: With finding 1 the test seeds `permalink:102` and asserts that the row and the manifest entry keep it, which is the guard. The recapture with `Revised 0` is declined: a recapture matches rows by `SourceKey` equality in ingest, which the rotated-host test covers, and `ParserTests` asserts that the parser emits `permalink:<id>` for a reel without a video URL; a surviving key therefore matches by construction.

### 5. Performance: All good
Reviewers: performance. Checked: the normalization is two small queries per post with a flush every 500 posts, once per key version (10 s on the copy); `File.Exists` runs only inside duplicate groups; the `referenced` set loads about 25k paths once after the commit; `AddImage` is quadratic in the images of one post, which `Limit` caps at 8 to 10; `SourceKey` does a little less work than before; nothing grows.
