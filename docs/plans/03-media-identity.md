# Plan 03: Media identity from platform ids; content hash from bytes

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

A post's media slot is identified by the platform's own media id, with a stable fallback. A change of CDN host or URL grammar never bumps `ContentRevision` and never sends a post back to the model. Duplicate media rows for one slot stop appearing. Signed URLs leave the database once the file is on disk.

## Problem and evidence

`PayloadParser.SourceKey` (L34-39) derives media identity from the CDN URL (scheme, host, path, `stp` and `url` parameters). `Ingest.ContentHash` (L81-87) includes `{Kind, SourceKey, bytes}` per source. Media rows match sources by `SourceKey` in four places (L85, L116, L125, `FeedQuery.RepeatKey`). A URL change therefore looks like new content: `ContentRevision++`, `InvalidateModel`, re-judge.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| Duplicate media rows removed by key version 2 on 10-25 | 191,804 across 8,624 posts |
| Posts with `ContentRevision > 1` | 8,221 of 10,815 (max 382) |
| Posts created after 10-25 with `ContentRevision > 1` | 162 of 1,371 (11.8%) |
| (PostId, Position) groups with more than one row | 1,203 |
| `Media.OriginalUrl` plus `Post.MediaManifestJson` size | about 41 MB, 30% of live table data |

The parser reads no Facebook media id at all (media walk L517-530). For Instagram it reads `pk` for the post but not for carousel slides or `video_versions` (L113, L239, L455). The fixtures have no Facebook photo id and no `video_versions[].id`.

`Media` has no unique index (`FeedDbModelSnapshot.cs` L243). `MediaKeys.Normalize` (key version 2) runs once at `FeedDb.Initialize` and rekeys rows from `OriginalUrl` without a revision bump.

## Current behavior to keep in mind

- `MediaSource(Kind, Url, SourceKey)` (`Entities.cs` L109). `Limit` (PayloadParser L554) keeps distinct-key images up to 8 (Facebook) or 10 (Instagram), then one video.
- File path: `media/{platform}/{Safe(PlatformPostId)}/{pos:00}{suffix}{ext}`. The suffix is `-<sha(SourceKey)[..12]>` when another row holds the position (Ingest L127). Files are write-once (`MediaFiles.Image` L39-61).
- `Maintenance.Images` (L14-44) retries undownloaded rows; a late success recomputes the hash and bumps the revision (L36-39) because the judge reads pictures.
- Videos wait for the verdict (`Maintenance.Videos` L45-84). Video `ContentHash` is set but does not enter the post hash.
- `MediaManifestJson` readers: `MediaKeys.cs` L33, `Maintenance.cs` L37, `IdentityStore.cs` L50, `FeedQuery.cs` L170, `ParserAudit.cs` L26.

## Target design

1. **A `MediaKey` per source.** Add `MediaKey` to `MediaSource` and a `MediaKey` column to `Media`. Priority: platform media id when the payload has one; else the last path segment of the CDN URL without query (Meta filenames are stable per asset); else the current `SourceKey`. Key form: `id:<platform id>`, `file:<name>`, `url:<sourcekey>`, so the kind is visible.
2. **Parser reads the ids.** Facebook: the `id` of the `Photo` or `Video` node that holds the image or playable URL in the media walk (L517-530). Instagram: `carousel_media[].pk` for slides, the item `pk` for single media, `video_versions[].id` when present. Parser version 17.
3. **Hash from keys and bytes.** `Ingest.ContentHash` uses `{Kind, MediaKey, bytes}`; `SourceKey` leaves the hash. A late image download still changes the hash once, as today. A URL-only change does not.
4. **Match by `MediaKey`.** Replace the four `SourceKey` matches with `MediaKey`. `SourceKey` and `OriginalUrl` become fetch details for the pending download. Add a unique index on `(PostId, MediaKey)` filtered on `IsCurrent = 1`.
5. **Key version 3 normalization.** Extend `MediaKeys.Normalize`: for existing rows compute `MediaKey` from `OriginalUrl` with the filename fallback, collapse rows per `(PostId, MediaKey)` with the same keep-order as version 2 (file on disk, then hash, then newest), rewrite the manifest, recompute `ContentHash` without a revision bump, delete superseded files after commit. Kv `media:key-version = 3`.
6. **Drop signed URLs after download.** The daily job nulls `OriginalUrl` for rows with `Path` set and strips `Url` from manifest entries whose row is downloaded. A replay from raw restores URLs if a file is lost. Videos keep their URL until downloaded or pruned.

## Contracts to preserve

- Write-once files and the `NN` slot naming (7.2). The suffix rule stays for a replaced slot.
- Images download at ingest because signed URLs expire (7.3).
- Videos wait for the verdict; the hidden rule and the size cap stay (7.4).
- Content-revision invalidation still fires for real content changes: text, shared text, tags, a new image, a late image file.
- Raw replay remains the source of truth. Normalization never fabricates a key the raw cannot reproduce.

## Implementation sequence

1. Parser: extract ids and compute `MediaKey` with the three-tier rule. Update both fixtures to carry a Facebook photo id and an Instagram slide `pk`. Tests: a URL change with the same id gives the same key; a missing id falls back to the filename; a missing filename falls back to the source key.
2. Entities and migration: `Media.MediaKey`, the filtered unique index, manifest schema with `MediaKey`. Keep `SourceKey`.
3. Ingest and Maintenance: match and hash by `MediaKey`. Test: re-ingest the same post with a rewritten CDN host, assert `ContentRevision` unchanged and no new row. Test: a genuinely new image bumps the revision once.
4. `MediaKeys.Normalize` version 3 with a dry-run report line. Test on a synthetic database with duplicate slots: rows collapse, files kept match the keep-order, no revision bump, Kv updated.
5. `FeedQuery.RepeatKey` on `MediaKey` plus hash. Test: repeat folding still folds byte-identical images.
6. Daily URL cleanup. Test: downloaded rows lose `OriginalUrl`; pending rows keep it; replay restores it.

## Spec updates

- `docs/07-media.md` 7.2 L13-17 (identity is the media key, not the URL), 7.3 L22 (the fetch key rule moves under "download"), 7.7 (URL cleanup).
- `docs/05-data-model.md` L64-65 (manifest and content hash), L100-111 (Media columns, `MediaKey`, index), L287 (Kv `media:key-version` 3), 5.12 L449 (hash definition).
- `docs/06-collectors.md` L165 (Facebook media walk reads ids), L193-194 (Instagram slides and video ids).
- `IMPLEMENTATION.md` internal choices: media identity line.

## Verification

- Fast suite plus the tests above.
- Private instance: run normalization version 3 in dry-run first and record collapsed rows and freed bytes; then live. Confirm the feed renders identically and the "older policy" count is unchanged. Over the following week, the share of new posts with `ContentRevision > 1` should fall from about 12% toward the share with real edits.

## Open questions for the reviewer

1. Is the CDN filename an acceptable fallback for Facebook assets with no `Photo` node id? It is stable in practice but undocumented.
2. Should a late image download still bump the revision, or should the judge be re-run only when vision is enabled? Today it bumps regardless.
3. Null `OriginalUrl` after download, or keep it for debugging and accept the 30% storage share?
4. Should the backfill in step 4 reparse from raw where the raw still exists, to get real ids instead of filenames? It costs one pass over 35k files.

## Out of scope

Image retention bounds (review finding 7), video context (plan 10), author identity (plan 04).
