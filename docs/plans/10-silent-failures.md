# Plan 10: Stop silent feature failure

Status: implemented 2026-11-10 on the reduced course (plan review: CHANGE COURSE, smaller course taken with one adjustment; code review: GO). Reports in `.work/10-silent-failures/`.

## Outcome

A missing external tool shows on Manage and in `status` after the next processing run, with a recovery text. Video context asks yt-dlp only for shared external links (YouTube, Vimeo) and never replays the owner's platform session from a second client. The video download fallback runs without cookies. The Docker image carries a pinned, current yt-dlp and refuses to build when a tool is absent. No image is published without the fast test suite passing.

## Problem and evidence

`VideoContext` writes `Error = tool_missing` when yt-dlp is absent (`VideoContext.cs` L170). The only place that shows it is a progress line in `Processing.cs` L198. No status line, Manage notice or alert exists. A missing yt-dlp also prunes a video for good: `Maintenance.Videos` marks the row `PrunedAt` on the first `Win32Exception` (`Maintenance.cs` L80).

Live instance, 2026-11-09 and 2026-11-10 (counts only):

| Measure | Value |
|---|---|
| `data/video-context` cache entries | 256, all `tool_missing` |
| Verdicts marked stale when the feature was enabled on 10-30 | 4,877 |
| Posts with a video | 3,847, of which 3,321 are native Instagram |
| `video_context.enabled` today | absent from `config.json`, so false |
| Video media rows with a permalink-only source | 0 of 4,158 |
| Video rows pruned because yt-dlp could not start | 5 (all were expired CDN URLs that fell back to the tool) |

The Dockerfile installs yt-dlp from Ubuntu noble apt (L7, since the initial commit). That version is months old, and yt-dlp needs frequent updates. `.github/workflows/docker-publish.yml` has one job that builds and pushes `latest`, `main` and `sha-*` tags with no test step. Why the live host has no yt-dlp on its PATH is not known from the repository; the probe below shows the answer after the next deploy.

yt-dlp runs with the owner's `cookies.txt` for metadata (`VideoContext.cs` L113-118) and for the download fallback (`Maintenance.DownloadTool` L86-94). That is the owner's logged-in session replayed by a Python client with a different TLS and user-agent fingerprint, possibly while Chromium is active and even after a checkpoint. For a native Instagram reel the metadata call returns the caption the judge already has. Subtitles are rare on reels. The real gain is for shared external links such as YouTube.

## Current behavior to keep in mind

- `VideoContextConfig` (`Configuration.cs` L86-91): `enabled` false, `timeout_seconds` 30, `caption_languages` ["en"]. `VideoContextProvider.Version = 1`.
- `VideoContextProvider.Source` (`VideoContext.cs` L79-97) already decides which posts get a call: shared YouTube and Vimeo links without cookies, shared facebook.com and instagram.com reel links with the platform's cookies, and a native post with a current video through its permalink with cookies. The host filter of this plan is a deletion inside `Source`, not a new list.
- The cache lives at `data/video-context/<sha>.json`, 256 files at most, TTL 7 days on success and 30 minutes on failure (L155). A failure therefore re-spawns the missing tool every 30 minutes per video. The cache key holds `Version`, the source and the caption languages (L101).
- The parser already extracts direct video URLs: Instagram `video_versions[].url` (`PayloadParser.cs` L460, permalink fallback for reels at L461); Facebook progressive and `browser_native_*` URLs (L536-540, permalink fallback at L543). `Maintenance.Videos` calls `DownloadTool` for a permalink-only row (L72) and when the direct URL fails with an HTTP error or a timeout (L70), which is the expired-URL case.
- `cookies.txt` is exported by `BrowserSession.ExportCookiesOnClose` after collect, login and like (`Capture.cs` L37, L197; `LikeSender.cs` L52).
- `Prompts.ConfigurationHash` wraps `video_context` and its version into the verdict version when the feature is enabled (`Prompts.cs` L45, L67). Plan 02 kept that on purpose: the model sees the context. This plan does not change `Version`, so no verdict goes stale.
- Notices: `RecoveryIssue(Platform, Item, Problem, Attempts, LastAttempt, RetryAfter, Recovery, Informational)` with `RecoveryDisposition` (`RecoveryQuery.cs` L7-21). `AttentionPanel` groups by disposition; `Reports.Status` prints `recovery <platform>/<item>: ...` lines (L55) and the backup line from plan 08 (L56). `Management.PlatformName` (L32) and `RecoveryDetails` assume `Platform` is a platform name.
- `ffmpeg` shrinks vision images (`MediaFiles.cs` L70); when it is missing, `Vision` returns the original bytes with `Fallback = true` (L77-78), so classification still works. `ffprobe` reads the video size (`Maintenance.cs` L107-119) and its absence only leaves width and height null.
- The processing child is the only process that spawns tools. `Processing.Run` (`Processing.cs` L23) runs `Maintenance.Images` and `Videos` (L69-79), vision (L141) and the video context (L189-203). `Processing` is built with `new` in `Program.cs` L135; there is no DI container.

## Target design

1. **Tool probe in the processing child.** New `ToolHealth` in Infrastructure runs `yt-dlp --version` and `ffmpeg -version` with a 10 second timeout, once at the start of `Processing.Run`. It stores Kv `tools:yt-dlp` and `tools:ffmpeg` as JSON `{ "status": "ok" | "missing" | "error", "version": "...", "at": "..." }`. The web host spawns nothing (binding rules 3 and 8 stay as they are). A tool installed later is found by the next run. `Maintenance.Videos` skips the yt-dlp fallback while the key says `missing` or `error` (a tool that cannot answer a version call cannot download either), counts no attempt for it, and no longer prunes a row on `Win32Exception`; the row waits for the tool.
2. **Notices.** `RecoveryQuery` emits one issue per missing tool with `Platform = "tools"` and `Item` the tool name. yt-dlp missing is Attention when `video_context.enabled` is true and Informational otherwise (the download fallback is a last resort). ffmpeg missing is Informational when `llm.vision` is true (vision still works with the original image) and absent otherwise. No issue while a key is absent. Each issue carries a Recovery sentence: "Install yt-dlp (docs 16.2) or set video_context.enabled to false; the next processing run clears this." `RecoveryPresentation` gets a `tools` category with title and explanation; `Management.PlatformName` and `PlatformGlyph` learn the value `tools`. `status` prints one `tools:` line (`tools: yt-dlp 2026.10.26, ffmpeg 6.1.1`, or `tools: yt-dlp missing (checked <time>), ...`, or `tools: not checked yet`), built by `ToolHealth.Line`. The Manage AI processing panel shows the same line.
3. **No failure streak.** After item 4 the feature reaches only posts with a YouTube or Vimeo link (about 72 on the live copy). A broken extractor costs one timeout per such post, once, and shows in the progress log and in the `error` field of the cache entry. A counter, a pause and a notice for that were dropped at review.
4. **Video context only where it adds information.** `VideoContextProvider.Source` returns a source only for shared YouTube and Vimeo links. The Facebook and Instagram branches, the native permalink branch, `VideoSource.CookiePlatform` and the cookie copy path (L113-118) are deleted. `Version` stays 1: native posts received `tool_missing` before, so the content the model sees does not change, and the verdict version must not move. The old cache files for native sources age out through the 256-file cap.
5. **Download fallback without session replay.** `Maintenance.DownloadTool` runs yt-dlp without cookies. The cookie copy lines are deleted. No config key: the live copy shows 0 permalink-only rows and 5 fallback attempts in total, all against the missing tool, so no working path is lost. An expired Facebook or Instagram CDN URL now falls back without a session; when that fails the row is pruned after 3 attempts as today and the post keeps its preview image.
6. **Pinned yt-dlp in the image.** Dockerfile `ARG YTDLP_VERSION=2026.10.26` and `ARG YTDLP_SHA256=<hex from the release's SHA2-256SUMS, stored in the repo>` download the standalone asset `yt-dlp_linux` from the GitHub release, check the hash with `sha256sum -c`, and install it to `/usr/local/bin/yt-dlp`. The apt `yt-dlp` package goes; the standalone build needs no python3. Both ARGs change in one commit. `RUN yt-dlp --version && ffmpeg -version && dotnet Feed.Cli.dll --help` after the install makes the build fail by itself when a tool is absent. `docs/16-deployment.md` 16.2 documents the pin and the same install for direct Linux; the RUNBOOK gets the bump step.
7. **Tests before publish.** The workflow gains a `test` job that runs in the `mcr.microsoft.com/dotnet/sdk:10.0.403` container (the same image as the Dockerfile build stage, so no new action to pin): checkout, `dotnet restore Feed.slnx --locked-mode`, `dotnet test tests/Feed.Tests -nologo -v q --filter 'Category!=Browser'`. `publish` gets `needs: test`. The smoke check lives in the Dockerfile (item 6), so the single build-push step stays. The `publish` job keeps its `main` guard; a `workflow_dispatch` from a branch runs the tests only.
8. **Manage.** No video context toggle (the form has none today, and the key stays a config-file setting). The AI processing panel shows the `tools:` line from item 2, the same pattern as the backup line of plan 08. The Attention row of item 2 is the signal on the overview.

## Contracts to preserve

- Binding rule 3 and rule 8: the web host spawns no tool. The probe runs in the processing child.
- The verdict version does not change: `VideoContextProvider.Version` stays 1 and `video_context` is absent from the live config.
- Video judgments keep using captions, preview images and extracted metadata where available (7.8). Native videos lose nothing they receive today, because the tool is missing anyway and the caption is already in the prompt.
- Binding rule 7 and the capture path are untouched.
- No new platform write. Cookies never leave the browser profile: no process other than Chromium reads `cookies.txt` after this plan, except the like-back export that writes it.

## Implementation sequence

1. `ToolHealth` probe (Kv keys, `Line`), the call at the start of `Processing.Run`, the `Videos` skip and the prune fix, the `status` line. Tests: a fake `ProcessStartInfo` is not available, so `ToolHealth.Probe` takes the tool names and a resolver; the test passes a missing name (`feed-test-missing-tool`) and expects `missing`, and a present one (`dotnet --version`) and expects a version. `MediaTests` gets a case: the fallback is skipped while the key says `missing` and the row keeps `PrunedAt = null`.
2. `RecoveryQuery` tool issues, `RecoveryPresentation` category, `PlatformName`/`PlatformGlyph`, the Manage line. Tests in `RecoveryPresentationTests` and a query test with the Kv key set and the feature enabled.
3. `Source` reduction and the cookie path removal in `VideoContext`. Update `VideoContextTests`: a native Instagram URL and a shared Facebook reel return no source; a YouTube URL does; the cookie snapshot test goes.
4. `DownloadTool` without cookies. Existing `MediaTests` keep covering the fallback.
5. Dockerfile pinned binary with checksum and the smoke `RUN`. The image cannot be built in the sandbox; the Dockerfile is read-checked.
6. Workflow `test` job and `needs`. Verified by a YAML parse and a dry read; the first real run is the next push to `main`.

## Spec updates

- `docs/07-media.md` 7.4 (fallback without cookies), 7.8 (external links only).
- `docs/09-llm.md` 9.10 (video evidence scope, `tool_missing` surfaced).
- `docs/16-deployment.md` 16.2 tool table (pinned yt-dlp, probe), 16.3, 16.10 (CI test gate, Dockerfile smoke).
- `docs/04-instance.md` `video_context` (external links only; the Attention row when yt-dlp is missing).
- `docs/12-scheduler.md` 12.5 (the probe at the start of each processing run; no daily step).
- `docs/13-cli.md` `status` tools line.
- `docs/15-ui.md` Manage tools line and the tools recovery category.
- `docs/17-testing.md` 17.1 (CI gate), video context regression section.
- `docs/RUNBOOK.md`: tools section (missing tool, version bump).
- `docs/plans/README.md`: the dependency row of plan 10 (plan 02 kept video context in the verdict version; this plan needs nothing from 02).

## Verification

- Fast suite plus the tests above. The workflow file parses; its first run is the next push to `main`.
- Private instance: after deploying the new image, `status` shows a yt-dlp version, the Attention issue clears, and the cache gains `available` entries for shared external links only. No cookie copy appears under `video-context/private/` or in the temp folder.

## Decisions taken at review (2026-11-10)

1. The probe runs in the processing child, not in the web host and not in the daily job.
2. The cookie option for the download fallback is dropped; the fallback runs without cookies.
3. The failure streak is dropped: no counter, no pause, no notice.
4. `video_context.enabled` stays false by default.
5. Step back (CHANGE COURSE) asked to keep only the deletions, the prune fix, the pin and the CI gate. The owner proxy took that course with one adjustment: the probe, the notices and the `status` and Manage lines stay, because a visible missing tool is the first priority of this plan and the prune fix alone would make waiting video rows a new silent failure. The probe is about 30 lines in one class and spawns two version calls per processing run.

## Out of scope

Verdict version contents (plan 02), frame sampling or transcription (`docs/VIDEO-UPGRADES.md`), the like-back cookie export after likes, a Manage toggle for `video_context.enabled`, an automatic yt-dlp update.

## Notes from implement

- The Dockerfile smoke `RUN` checks `yt-dlp --version` and `ffmpeg -version` only. `Feed.Cli --help` opens an instance before it prints the usage (`Program.cs` L47-L65), so it cannot run in the image build; the `browser install-deps` step already proves the CLI starts.
- The tool check runs for the scheduled `process` kind only, not for manual `rescore` and `summarize` runs: those run inside the test suite and from the CLI, where two extra spawns buy nothing. The spec puts the check in 12.9 (independent processing), not 12.5 (the daily job has no step).
- `Maintenance.Videos` treats a missing yt-dlp the same whether the probe said so or the spawn failed: the row keeps its attempt count, records the error and waits 30 minutes. A row that never moved its attempt stamp would head every batch and keep the processing loop selecting it, so the stamp moves.
- `VideoContextProvider.Source` takes the post only; the media list served the native branch that is gone. `IVideoMetadataTool.Extract` lost its cookies parameter.
- The `tools` pseudo-platform renders through the existing `PlatformGlyph` text fallback; only `Management.PlatformName` needed the value.
- The workflow `test` job runs in the SDK container. It is validated by a YAML parse and a dry read only; the first real run is the next push to `main`. The image was not built in the sandbox (no Docker build, no external binary fetch); the yt-dlp hash was read once from the release's `SHA2-256SUMS` for 2026.10.26.
- A recovery-row test stores the Kv states directly instead of probing, because a developer machine may have both tools installed.
