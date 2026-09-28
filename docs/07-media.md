# 7. Media

You are here: the files on disk. Chapter 6 captured the posts and their media URLs. This chapter specifies which files are downloaded, where they live, when the video waits, and when files are deleted again. The rules limit retained media while preserving post records. They do not promise a fixed disk bound: visible images and metadata can grow with history.

Source map: [MediaFiles.cs](../src/Feed.Core/Infrastructure/MediaFiles.cs) · [Maintenance.cs](../src/Feed.Core/Application/Maintenance.cs) · [MediaTests](../tests/Feed.Tests/MediaTests.cs).

## 7.1 Why local files

Signed CDN URLs expire, on Instagram within hours. A card must show its pictures a month later. So images are downloaded in the capturing run. Videos are the instance's weight, so they wait for the verdict and follow retention rules.

## 7.2 Layout and naming

- Post media: `media/<platform>/<safe post id>/NN.ext` for an initial slot. A replacement source gets a new immutable filename `NN-<source hash>.ext`; it never overwrites bytes at an old URL. NN is the slot number from 01. Rows carry SourceKey and IsCurrent; cards and judging use only current rows.
- Avatars: `media/avatars/<platform>/<safe author id>.ext`.
- A safe segment keeps ASCII letters, digits, `.`, `_` and `-`. Any other character becomes `_`. An empty result becomes `unknown`.
- An image's extension comes from the URL when it is one of `.jpg`, `.jpeg`, `.png`, `.gif`, `.webp`, else `.jpg`. Avatars follow the same rule. A directly downloaded video is always `NN.mp4`. A video fetched through `yt-dlp` takes the extension the tool picks.
- Write-once. A URL and a file are immutable. An existing non-empty file in a slot is registered, never fetched again. That is what makes an immutable cache header on the media route safe, and what lets an offline reparse register the files of an old run.

## 7.3 Images

At ingest, reconcile the parser manifest with current Media rows, including recovery of a partially completed post:

- Take the first N image URLs in payload order (Facebook 8, Instagram 10), slots 1 to N. Derive SourceKey using a platform media id when available, else an adapter-normalized URL that removes only known transient signing fields. Reuse matching files; retire removed/replaced identities and fetch missing slots. Compute and persist file-byte hashes.
- Download each with a 60 second timeout, following redirects. Pause 500 to 1500 ms after each download.
- Read and store width and height from the actual file for JPEG, PNG, GIF and WebP. A small header reader is the reference implementation; a maintained image-inspection library is also allowed. Inspection is bounded, handles malformed inputs, and needs no browser/network access. Dimensions support layout without shifts. A file that cannot be sized is kept unsized.
- The glyph guard: a file whose both sides are under 64 px is deleted and gets no row. A one-pixel placeholder from a link gateway or a page avatar would otherwise fill a card. A wide banner passes, because one side is large. The guard runs at download and again when an existing file is registered. It applies to post images only, never to avatars.
- A failed download deletes the partial file and leaves no row. A dropped slot leaves a gap in the positions.
- The download never throws. Ingest continues with the next post.

## 7.4 Videos wait for the verdict

The judge reads images only, so it loses nothing by the wait. Deterministic filters run in the insert transaction. The judge normally follows a few minutes after capture, inside the CDN URL's life; delayed recovery may encounter an expired URL and follows the ordinary download failure rules.

- One video per post: the first progressive video URL. When none exists but the post carries a video hint, the source is the permalink, to be fetched with `yt-dlp`.
- The slot number is the number of images taken (the image count, capped at the image limit) plus one. So the video may sit at slot 9 on Facebook and 11 on Instagram.
- At ingest, create or retain a pending row for a current video source of a deterministically visible post. A hidden post gets no new video slot. An offline ingest registers existing files but does not create network work. Source identity and current-slot rules match images.
- Independent processing selects successive bounded batches of due pending videos, oldest attempt-or-created time first, subject to an explicit per-stage limit when supplied. A row attempted within 30 minutes is not due. Read visibility and current content afresh, not from a tracked ingest entity.
  - Hidden post: stamp pruned without fetching.
  - Visible, model enabled, no current-content verdict: wait. A broken model does not trigger unbounded video downloads.
  - Visible with a current-content verdict, or model disabled: stamp the attempt and fetch without a browser lock. Direct URLs have the size cap and 60 second timeout. A permalink source uses yt-dlp with the existing exported cookies, 300 second timeout and the same cap. Read a private snapshot of the atomically exported cookie file; do not open the profile or refresh login.
  - A failed expiring direct URL may fall back to the permalink through yt-dlp within the same attempt. On success, register the immutable file only if the source is still current; otherwise discard the unregistered file. On failure keep the row, increment DownloadAttempts, record Error and wait 30 minutes. After three failures, or an oversized file/missing required tool, stamp PrunedAt with the reason. An explicit later repair can be designed separately; ordinary ingest never refetches a pruned identity.

Model work and pending videos no longer wait for another successful collection. Capture, processing and media failures have separate counts.

## 7.5 Avatars

The friends import downloads each person's profile picture into the avatars folder. An existing file is returned without a fetch. `avatars --platform X` backfills from the stored friends raws for authors without one. Avatars skip the glyph guard and retention.

## 7.6 Vision input

The images the model sees are derived from the stored files for the prepared content revision. The baseline input policy is:

- current image rows with a file, in position order, until `vision_max_images` images have loaded; a dropped file does not use up a slot. Record the digest of the actual resized bytes sent in the model input hash;
- a file over 8,000,000 bytes, a missing file or an empty file is dropped;
- a file whose long side exceeds 768 px is shrunk to a 768 px JPEG with aspect ratio preserved and an even short side; `ffmpeg` quality 4 is the current encoder setting;
- without a working supported resize processor, the original bytes go out and provenance records that fallback;
- the MIME type is sniffed from the magic bytes, and unknown bytes are dropped.

Shrinking limits vision-input cost. Actual latency depends on the endpoint and workload; chapter 9 defines the call.

**Implementation note.** The current processor uses FFmpeg. Its subprocess choice is not a compatibility requirement. An equivalent processor may meet the same size/format/aspect contract. The current byte limit, dimensions and configured image count remain the baseline model-input policy; changes affecting judgment are versioned in model configuration and exact input provenance. Keep prepared bytes transient or in a bounded private cache keyed by source-byte digest and all preprocessing settings/processor version. Reuse never serves a different revision or overwrites the original media. Memory-only versus disk caching is an implementation choice; private cached inputs are never published by a media route. Always hash the actual bytes sent, including originals used as fallback.

## 7.7 Retention

The scheduler runs one maintenance job on the first tick of each UTC day (chapter 12), only while the scheduler is enabled. It marks the day first, so a failing job runs once a day, then runs raw retention, media retention and the disk measurement. The CLI runs the same code by hand with `raw prune` and `media prune`, both with `--dry-run`.

**Raw retention.** `raw_retention_days` (60; 0 never).

- Cutoff: now minus the days. A snapshot of a run that ended `capped` or `checkpoint` uses twice the days: those captures are the most likely to be re-run. `--before <date>` replaces both cutoffs.
- Only successfully parsed snapshots captured at or before cutoff are eligible. Never prune unparsed captures or a directory still being captured/ingested. Coordinate with ingest ownership; a busy operation defers deletion. Delete the eligible file, mark the row deleted, and remove an empty directory. Unparsed failures remain visible in status so they can be repaired.
- Every run directory whose run finished at or before the cutoff and that has no live snapshot left is removed with everything in it, screenshots included.
- Pruning an eligible run directory also removes its matching `media/diagnostics/<platform>/<run dir>/` screenshot copies. These copies follow raw retention, are excluded from post-media retention, and are not Media rows.
- Only `RawSnapshots` rows change. Posts keep their `RawRef`. A post whose raw is gone still renders, scores and likes normally; only "reparse this old capture" stops being possible.

**Media retention.** Coordinate with processing and ingest ownership; never remove an input while a worker prepares it. Retired media is not rendered and follows the same retention policies. Each file is counted once. Rows always stay: `Path` null, `PrunedAt` set, and an emptied post folder is removed. Ingest never re-downloads a pruned row, and the card shows one muted line with the original linked.

1. `hidden_media_retention_days` (7): every file of a post hidden longer than this. The grace runs from `HiddenAt`, not from capture, so a policy edit that hides old posts leaves the owner a week to look at them with their pictures.
2. `hidden_video_retention_days` (1): the video of a hidden post after this shorter grace. A review of the hidden view needs the pictures, not the video. An unhide after the prune shows the card without its video.
3. `video_retention_days` (180): videos of posts older than this by post date. Images, text and permalink stay, so the card still reads. A post without a date is exempt.
4. Orphans: under `media/facebook` and `media/instagram` only, a folder whose name matches no post id of that platform and is older than one day is removed. Avatars are never scanned.

A value of 0 turns a policy off.

**At the door.** Three rules stop the weight from arriving:

- `max_video_mb` skips an oversized video and keeps the poster;
- a post hidden at insert never gets a video slot;
- every other video waits for the filters and the judge (7.4).

**The disk line.** The daily job measures `media/` (with the video share), `raw/` and the free space of the data drive, and stores one line in `Kv`: `media X GB (videos Y GB) · raw Z GB · free W GB`. The status command and the debug page print that line, so neither walks thousands of files per request.

Retention never deletes posts, authors, media rows or runs.
