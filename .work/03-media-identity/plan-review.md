# Round 1 (2026-11-10)

Target: `docs/plans/03-media-identity.md`

Reviewers: neutral, minimalist, skeptic, performance (reason: a normalization pass over 25,877 rows at startup and a key computation per source), ux (reason: cards lose a duplicate rendition slot, and the rendition choice is visible). Security was left out: the only input is the CDN URL the parser already handles.

Before the review the session corrected the plan's anchors against main at 21e1f35, measured a copy of the live database (duplicate groups, what differs inside them, byte equality, renditions), scanned 25 raws per platform read-only for media id shapes, and had an agent diff the hash inputs of 40 Facebook and 10 Instagram recapture pairs. On that evidence the session moved the design from a platform-id tier with a new column and parser version 17 to the filename key under the existing key-version mechanism, and listed the dropped parts as open questions for the reviewers. The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: Media-key churn caused 30 of the 40 sampled Facebook revision bumps, and the 1,216 duplicate groups on the live instance confirm it. The fix changes one key function and reuses the key-version 2 normalization path, so it adds no column, no index and no parser bump.

The step-back agent compared the cheaper alternatives: doing nothing (each needless bump sends a post back to the model, and duplicate rows reach the cards); keying on byte hashes (a rotated host returns different bytes in 523 of 948 groups); the dropped mechanisms (each removal has a reason); the widest-rendition rule (a few lines, and without it card quality gets worse for about two thirds of the 156 two-rendition posts). Shared-link and tag churn stay out of scope, which it judged correct.

User (owner proxy, 2026-11-10): GO. Implement as planned.

## Skeptic's cons

1. **The `file:` rule covers every URL with a filename, not only Meta assets.** (Target design 1, second bullet.) Generic names like `image.jpg` or `default.png` from other hosts can merge two different pictures into one slot. When that happens, a real picture change does not bump the revision. Price: the judge does not see real changes, and nothing reports it. The "no rename observed" evidence tests the opposite failure.
   Answer: Agree: the rule now applies only to hosts ending in `fbcdn.net` or `cdninstagram.com`; every filename-keyed row on the live instance is on one of the two (25,545 and 15), so the condition costs nothing today and keeps a generic name on another host at today's URL key.
   User (owner proxy, 2026-11-10): adopted (decision 6 in the plan).

2. **The plan checks the migration on a copy that has no media files.** (Target design 3; Verification, second bullet.) That copy uses a different keep-order (byte hash) from production (file on disk). The migration deletes rows and files, and the plan adds no dry-run. Price: the live run keeps different rows than the test did. The deletion cannot be undone.
   Answer: Agree: the verification now mirrors the `media/` tree as empty files (13,375 files), because `File.Exists` is all the keep-order reads. The keep-order itself is a unit test. No dry-run command: `Normalize` deletes a superseded file only after the commit and only when no row references it.
   User (owner proxy, 2026-11-10): adopted in Verification.

3. **One filename can stand for different crops or sizes, and the collapse does not prefer the larger rendition.** (Target design 3 and 4.) Price: the card can show a thumbnail for good. Positions after the collapse (gaps or renumbering) are also not specified.
   Answer: Agree on the rendition: see finding 2. On positions: the collapse leaves a gap, the card and the vision input order by `Position` (`FeedQuery.cs` L115, `Processing.cs` L134), and the next ingest renumbers (`Ingest.cs` L127); this is version 2 behavior and is unchanged.
   User (owner proxy, 2026-11-10): the widest rendition wins (decision 4).

4. **Without a unique index, only the matching logic keeps one row per key, and that logic already produced 1,216 duplicate groups.** (Decision 3.) Price: duplicates can come back from a race or another bug, and nothing rejects them.
   Answer: Disagree: the 1,216 groups came from the key rule, not from the matching. Each one holds rows with different `SourceKey` values of one filename (host or `stp`), so the match worked as written and the rule was wrong. A unique index would also fail at upgrade (the migration runs before the collapse) and would fail the raw at ingest instead of keeping the post.
   User (owner proxy, 2026-11-10): no index.

5. **The success metric has no target number and no baseline for real edits.** (Verification, third bullet.) Shared-link churn (16 of 40 pairs) stays in the data and hides the result. Price: after a week, nobody can say if the plan worked.
   Answer: Agree: the metric is now direct. Among new posts with `ContentRevision > 1`, the count whose media rows share a filename must be zero. That measure is independent of shared-link churn.
   User (owner proxy, 2026-11-10): adopted in Verification.

6. **The migration recomputes `ContentHash` for nearly every post during `Initialize`, and the plan does not list what else reads that hash.** (Target design 3.) Price: startup blocks for the whole migration; any reader that compares hashes can start a re-judge.
   Answer: Disagree: `Judged` compares `VerdictContentRevision` with `ContentRevision` (`Entities.cs` L44), not the hash; the hash is compared only against the next ingest of the same post (`Ingest.cs` L115), and that comparison is the point of the recompute. Version 2 ran the same pass on the same database without a re-judge, and the performance reviewer puts the pass at seconds.
   User (owner proxy, 2026-11-10): no change.

7. **The "first cause" claim rests on 40 Facebook pairs and 10 Instagram pairs.** (Evidence.) Price: a sample this small can rank the causes wrong.
   Answer: Agree in part: the ranking of the second cause rests on the sample, but the first cause does not. The duplicate-group measure covers all 25,877 rows (1,216 groups of one filename with different keys), and the plan fixes that cause. Shared-link churn goes to a follow-up with its own measurement.
   User (owner proxy, 2026-11-10): no change.

## Findings

### 1. Normalization rekeys `permalink:` video rows from the permalink URL: Major, FIX
Reviewers: neutral, minimalist. Where: Target design 1 and 3; `MediaKeys.cs` L26 and L34; `Ingest.cs` L130; `PayloadParser.cs` L456 and L530
Finding: The parser gives a reel without a video URL the key `permalink:<id>` and the permalink as the URL. Ingest stores the permalink as `OriginalUrl`. `Normalize` rekeys every row and every manifest entry from the URL, so the key becomes the URL key. The next recapture emits `permalink:<id>` again, finds no row, adds a video row and bumps the revision. Version 2 did this to the 10 permalink rows on the live instance (all rekeyed, none keeps `permalink:`). The planned test "a `permalink:` source is untouched" checked `SourceKey`, which never sees that key.
Resolution: Item 3 keeps a `permalink:` key on rows and manifest entries. Step 3 asserts it in the version 3 test with a recapture that gives `Revised 0`. The `SourceKey` test line is removed.

### 2. The collapse can leave the card with the smaller rendition: Major, ESCALATE
Reviewers: ux; skeptic con 3. Where: Target design 3 and 4; `MediaKeys.cs` L28 (keep-order); `PayloadParser.cs` L554 (`Limit`)
Finding: In 100 of the 156 live two-rendition posts the lower slot is the smaller rendition. First-seen would keep the thumbnail for new captures, and the version 2 keep-order (newest `Id` after file and hash) would keep it for existing posts. The reader sees the card at full width; the 768 px vision cap does not apply to the card.
Resolution: Question for the owner. Recommended: yes, the widest wins. The keep-order gets "largest stored `Width`" after "file on disk" (`Width` is read from the file header at download, so no file stat). The Facebook walk reads `image.width` and keeps the wider entry per key at the first-seen position.
User (owner proxy, 2026-11-10): yes. The owner reads the feed daily and the cost is a few lines (decision 4).

### 3. Step 4 did not name the assertion that changes: Nit, FIX
Reviewers: neutral. Where: Implementation step 4; `MediaTests.cs` L30
Finding: `RecaptureWithRotatedCdnParametersKeepsRevisionRowsAndFiles` asserts the version 2 key; the plan said only "extend".
Resolution: The step now says the expected key becomes `file:a_n.jpg`.

### 4. Two spec lines described no work: Nit, FIX
Reviewers: minimalist. Where: Spec updates
Finding: The `06-collectors.md` and `IMPLEMENTATION.md` lines said "no change" and "verify and leave".
Resolution: Both deleted. The `06-collectors.md` line is back with content, because decision 4 changes the walk.

### 5. The verification never reaches the "file on disk" rule: Nit, FIX
Reviewers: ux; skeptic con 2. Where: Verification; Implementation step 3
Finding: A database copy without files tests the fallback keep-order, not the live one, and no test had two files of different size.
Resolution: The copy mirrors the media tree as empty files; step 3 adds the two-files-different-width case.

### 6. Performance: All good
Reviewers: performance. Checked: no new column, index, manifest field or table; the key computation is equal or cheaper per source; the normalization is one pass with two small queries per post and a flush every 500 posts, seconds on local SQLite, run once; no cache, queue or batch; the recompute leaves `ContentRevision` unchanged, so the upgrade sends nothing to the model.
