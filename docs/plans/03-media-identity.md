# Plan 03: Media identity from the CDN filename; content hash stable across URL rotation

Status: proposed 2026-11-09 from the step-back review. Corrected on 2026-11-10 against main at 21e1f35 and a copy of the live database, then reviewed the same day (`.work/03-media-identity/plan-review.md`). Not started.

## Outcome

A post's media slot is identified by the asset's CDN filename, with the current URL-based key as the fallback. A change of CDN host, path prefix or rendition parameters never bumps `ContentRevision` and never sends a post back to the model. Duplicate media rows for one asset stop appearing, and two renditions of one picture occupy one slot.

## Problem and evidence

`PayloadParser.SourceKey` (L34-39) derives media identity from the CDN URL: scheme, host, path, and the `stp` and `url` query parameters. `Ingest.ContentHash` (L82-88) includes `{Kind, SourceKey, bytes}` per source. Media rows match sources by `SourceKey` in four places (Ingest L86, L117, L126; `FeedQuery.RepeatKey` L171). When Meta serves the same file from another CDN host, the key changes and the post looks like new content: a new row, a new download, `ContentRevision++`, `InvalidateModel`, re-judge.

Live instance, copy of 2026-11-10 (aggregates only):

| Measure | Value |
|---|---|
| Posts; media rows | 10,856; 25,877 (21,719 images, 4,158 videos) |
| Posts with `ContentRevision > 1` | 8,244 (max 382) |
| Posts captured after 10-25 with `ContentRevision > 1` | 180 of 1,412 (Facebook 170 of 626, Instagram 10 of 786) |
| Rows whose URL ends in an asset filename (`<n>_<n>_<n>_n.ext`, `<token>.mp4`) | 25,560 of 25,877; 306 are proxied external previews (`url=` parameter); 11 have no filename |
| Groups of rows with one filename in one post (duplicate identities) | 1,216 groups, 1,341 surplus rows |
| What differs inside those groups | Facebook images: host only 948, `stp` only 150, host and `stp` 18; Facebook videos: host only 71; Instagram: 28 |
| Rows with a different filename at the same (PostId, Position) | 1,268 slot groups; where both rows have bytes, the bytes always differ: a different picture, not a renamed one |
| Posts with two current rows of one filename (two renditions in two slots) | 156, all Facebook; in 100 the lower slot is the smaller rendition |
| Same filename, same `stp`, host rotated: downloaded bytes | 425 groups equal, 523 groups differ, and 520 of those differ in pixel size. The CDN picks the rendition by the signed parameters (`oh`, `ctp`), so no visible parameter fixes the rendition |
| `Media.OriginalUrl` plus `Post.MediaManifestJson` | 17 MB plus 24 MB, of a 412 MB database |

Parser diff of 40 recent Facebook raw pairs (same post, two captures, revision bumped): the media key differed in 30 pairs, 27 of them with the same filename set; `SharedUrl` differed in 16, `TagsJson` in 7, text in 1. Ten Instagram pairs: media key in 5 (3 with the same filename set), `ObservedAuthorName` in 4, `TagsJson` in 3. The media key is the first cause of needless revisions. Shared-link and tag churn are the second cause and stay out of this plan (see "Out of scope").

Platform ids in the raws (25 newest raws per platform, read-only): Instagram carousel slides carry `pk` (552 of 552); `video_versions[]` entries carry no id (220 of 220). Facebook image nodes carry a numeric `id` in 59 of 113 cases (`Photo`, `Video`, `ProfilePicAttachmentMedia`); the other 54 (link previews, text backgrounds, untyped `image` objects) have no id and no identified parent. The fixtures have no Facebook photo id and no `video_versions[].id`.

`Media` has no unique index on the key (`FeedDbModelSnapshot.cs` L231-291). `MediaKeys.Normalize` (key version 2) runs once at `FeedDb.Initialize` and rekeys rows from `OriginalUrl` without a revision bump. Spec 7.3 names this mechanism for every change of the key rule.

## Current behavior to keep in mind

- `MediaSource(Kind, Url, SourceKey)` (`Entities.cs` L109). `Limit` (PayloadParser L554) keeps distinct-key images in payload order up to 8 (Facebook) or 10 (Instagram), then one video.
- File path: `media/{platform}/{Safe(PlatformPostId)}/{pos:00}{suffix}{ext}`. The suffix is `-<sha(SourceKey)[..12]>` when another row holds the position (Ingest L128, Maintenance L24 and L55). Files are write-once (`MediaFiles.Image` L39-60).
- `Maintenance.Images` (L14-44) retries undownloaded rows; a late success recomputes the hash and bumps the revision (L36-39) because the judge reads pictures.
- Videos wait for the verdict (`Maintenance.Videos` L45-84). A video row's `ContentHash` is set but does not enter the post hash.
- `MediaManifestJson` readers: `MediaKeys.cs` L33, `Maintenance.cs` L37, `IdentityStore.cs` L73, `FeedQuery.cs` L170, `ParserAudit.cs` L26. All deserialize `MediaSource[]`.
- A bump of `PayloadParser.Version` re-admits unparsed blocked raws (`Processing.cs` L50) and re-indexes the diagnostics of raws with a warning (`CaptureIndex.cs` L24). It never replays the 36,703 parsed raws, and the posts keep their stored keys.

## Target design

1. **Key version 3: the filename is the identity.** `PayloadParser.SourceKey` keeps its signature and returns, in priority order:
   - for a proxied external preview (a `url` query parameter): today's key, the URL without fragment with `stp` and `url` only. The proxied origin is the identity.
   - for a URL on a Meta CDN host (the host ends in `fbcdn.net` or `cdninstagram.com`) whose last path segment is a filename with an extension: `file:<segment>`. Meta names an asset once (`<n>_<n>_<n>_n.jpg`, `<token>.mp4`); the host, the path prefix and the rendition parameters vary per request and per CDN edge. The host condition keeps a generic name such as `image.jpg` on another host from joining two pictures; every filename-keyed row on the live instance is on one of the two suffixes (25,545 and 15).
   - for anything else: today's key.
   The `permalink:<id>` key for a video fetched through yt-dlp is set by the parser, not by `SourceKey`, and stays. `MediaKeys.Version` becomes `"3"`.
2. **No parser version bump, no new column, no manifest change.** `SourceKey` stays the one identity column, `MediaSource` keeps its three fields, and `ContentHash` stays `{Kind, SourceKey, bytes}`. The parser does not read platform media ids (open question 1).
3. **Normalization version 3.** `MediaKeys.Normalize` runs as today under the new version: rekey rows from `OriginalUrl`, collapse rows per `(PostId, SourceKey)`, rewrite the manifest with `DistinctBy` on the new key, recompute `ContentHash`, no revision bump, delete superseded files after the commit. Two changes to the version 2 code:
   - A row or a manifest entry whose key starts with `permalink:` keeps its key. Version 2 rekeyed those rows from the permalink URL (`Ingest.cs` L130 stores the permalink as `OriginalUrl`), so the next recapture of a reel-only post found no row, added one and bumped the revision. The live instance has 10 such rows, all rekeyed.
   - The keep-order becomes: file on disk, then largest stored `Width`, then known byte hash, then newest. Two renditions of one picture now share a key, and in 100 of the 156 live cases the newer row is the smaller one.
   Kv `media:key-version = 3`, `media:key-migration` records the counts. The expected count on the live instance is about 1,341 removed rows (measured above); no dry-run command is added.
4. **The widest rendition wins a slot.** The Facebook media walk reads the `width` next to `image.uri` (or next to a bare `uri`). When two images share a key, the wider one replaces the earlier entry at its first-seen position; `Limit` is unchanged. Instagram already picks one candidate per node (6.9). The reader sees the card at full width, so the 768 px cap of vision input (7.6) is no reason to keep a thumbnail.

## Contracts to preserve

- Write-once files and the `NN` slot naming (7.2). The suffix rule stays for a replaced slot; existing files keep their names because `Path` is stored.
- Images download at ingest because signed URLs expire (7.3). An existing row with its file on disk is never fetched again (Ingest L127), so a host rotation costs no download.
- Videos wait for the verdict; the hidden rule and the size cap stay (7.4).
- Content-revision invalidation still fires for real content changes: text, shared text, tags, a new image (new filename), a late image file.
- Raw replay remains the source of truth. Normalization derives every key from a stored URL and never fabricates one.
- `OriginalUrl` stays on every row. It is the input that every key version rekeys from.

## Implementation sequence

1. `SourceKey` version 3 in `PayloadParser` with the rule comment updated. Tests in `MediaTests`: a host rotation gives the same key; a different `stp` on one filename gives the same key; a proxied preview with another `url` gives a different key; a URL without a filename, or a filename on another host, keeps the URL key; the existing `SigningTokensDoNotChangeMediaIdentityButRenditionsDo` test changes its rendition assertion to the new rule.
2. Facebook walk: widest `image` per key at the first-seen position. Test in `ParserTests`: two renditions of one filename in one story yield one image source with the wider URL, in the position of the first.
3. `MediaKeys.Version = "3"`, header comment extended with the version 3 reason, the `permalink:` guard and the width rule in the keep-order. The `KeyVersionTwoNormalizes...` test becomes the version 3 test: two rows of one filename from two hosts collapse to the row with the file on disk, the manifest holds one entry with the `file:` key, the revision is unchanged, the hash matches `Ingest.ContentHash`, the marker is `3`. Two more cases: two rows with files on disk and different `Width` keep the wider; a `permalink:` video row and its manifest entry keep their key, and a recapture gives `Revised 0`.
4. Ingest test: extend `RecaptureWithRotatedCdnParametersKeepsRevisionRowsAndFiles` so the second capture also rotates the host; the expected key becomes `file:a_n.jpg`; assert `Revised 0`, one row, one download, revision 1. Add a case where the second capture carries a different filename and assert the revision goes to 2 with a second row.
5. Spec updates (below), spec.html rebuilt.

## Spec updates

- `docs/07-media.md` 7.2 L13 (rows carry the key, not the URL, as identity) and 7.3 L23 (the key rule: filename first, proxied preview by origin, else the URL; version 3; the collapse keep-order).
- `docs/05-data-model.md` L131 (`SourceKey` meaning) and L307 (Kv `media:key-version` 3).
- `docs/06-collectors.md` L167 (the Facebook walk keeps the widest image per asset).

## Verification

- Fast suite, browser suite, `./build.sh`.
- On a copy of the live database in a scratch instance directory, with the `media/` tree mirrored as empty files (13,375 files; `File.Exists` is all the keep-order reads): run `status --data <copy>` so `Initialize` performs the version 3 normalization; read `media:key-migration`; confirm the removed-row count is near 1,341, that `sum(ContentRevision)` over all posts is unchanged, that no row lost its `Path`, that the 10 permalink rows keep their key, and that the kept row of a two-rendition post is the wider one. Do not run against `data/`.
- Over the following week: among new posts with `ContentRevision > 1`, the count whose media rows share a filename (the measure in the evidence table) should be zero. The overall share of bumped posts falls less, because shared-link and tag churn stay.

## Decisions (plan review, 2026-11-10; the owner is away, the session decided as owner proxy)

1. No platform-id tier. Instagram slides and posts carry `pk`; Facebook image nodes carry an id in about half of the cases; `video_versions` never do. The filename key covers every observed duplicate, and a renamed asset did not occur in 25,877 rows. The id tier would need a parser change, fixture edits and a second match path for rows keyed before the change. Reviewers agreed.
2. No daily null of `OriginalUrl` after download (the original item 6): it saves 17 MB of 412 MB and removes the input every key normalization rekeys from. Reviewers agreed.
3. No unique index on `(PostId, SourceKey)`: the migration runs before the normalization that collapses the 156 live duplicates, so the index would fail at upgrade, and an index violation at ingest would fail the raw instead of keeping the post. The matching logic keeps one row per key. Reviewers agreed; the skeptic's con 4 is answered in the report.
4. The widest rendition wins (item 4 and the keep-order in item 3). The ux reviewer showed that first-seen would put a thumbnail on the card for about two thirds of the two-rendition posts, now and after the upgrade.
5. A late image download still bumps the revision: the judge reads the picture (7.3). Unchanged.
6. The `file:` tier is limited to Meta CDN hosts (skeptic con 1).

## Out of scope

- Shared-link and tag churn: `SharedUrl` differed in 16 of 40 recapture pairs and `TagsJson` in 7, with the media key unchanged in 10 of them. This is the second cause of needless revisions and needs its own measurement of what varies (a tracking parameter, a different attachment pick, a tag slice). Candidate for a follow-up plan.
- Image retention bounds (review finding 7), video context (plan 10), author identity (plan 04, merged).

## Changes before review (2026-11-10)

- Anchors corrected to main at 21e1f35.
- The 11-09 evidence table is replaced by the 11-10 measurement; the raw sample and the parser diff of recapture pairs are added.
- The design moved from "platform id, else filename, else URL" with a new `MediaKey` column, parser version 17, a unique index and a daily URL cleanup to the filename key alone under the existing key-version mechanism. The reasons are in decisions 1 to 3. The reviewers judged this course; the review added the `permalink:` guard, the widest-rendition rule and the host condition.
