# Plan 10: Stop silent feature failure

Status: proposed 2026-11-09 from the step-back review. Not started. A fresh session reviews this plan before any code changes.

## Outcome

A missing or broken external tool shows on Manage and in `status` within one tick. Video context asks yt-dlp only for links where it adds information and never replays the owner's platform session from a second client. The Docker image carries a pinned, current yt-dlp. No image is published without the fast test suite passing.

## Problem and evidence

`VideoContext` writes `Error = tool_missing` when yt-dlp is absent (`VideoContext.cs` L170). The only place that shows it is a progress line in `Processing.cs` L199. No status line, Manage notice or alert exists.

Live instance, 2026-11-09:

| Measure | Value |
|---|---|
| `data/video-context` cache entries | 256, all `tool_missing` |
| Verdicts marked stale when the feature was enabled on 10-30 | 4,877 |
| Posts with a video | 3,847, of which 3,321 are native Instagram |

The Dockerfile installs yt-dlp from Ubuntu noble apt (L7). That version is months old, and yt-dlp needs frequent updates for Meta. `.github/workflows/docker-publish.yml` has one job that builds and pushes `latest`, `main` and `sha-*` tags with no test step.

yt-dlp runs with the owner's `cookies.txt` for metadata (`VideoContext.cs` L113-118) and for the download fallback (`Maintenance.DownloadTool` L86-94). That is the owner's logged-in session replayed by a Python client with a different TLS and user-agent fingerprint, possibly while Chromium is active and even after a checkpoint. For a native Instagram reel the metadata call returns the caption the judge already has. Subtitles are rare on reels. The real gain is for shared external links such as YouTube.

## Current behavior to keep in mind

- `VideoContextConfig` (`Configuration.cs` L94-99): `enabled` false, `timeout_seconds` 30, `caption_languages` ["en"]. `VideoContextProvider.Version = 1`.
- The cache lives at `data/video-context/<sha>.json`, 256 files at most, TTL 7 days on success and 30 minutes on failure (L155). A failure therefore re-spawns the missing tool every 30 minutes per video.
- The parser already extracts direct video URLs: Instagram `video_versions[].url` (L239, L455); Facebook `browser_native_hd_url`, `browser_native_sd_url`, `playable_url`, progressive URLs (L523-527). Only permalink-only sources need yt-dlp for the download.
- `cookies.txt` is exported by `BrowserSession.ExportCookiesOnClose` after collect, login and like (`Capture.cs` L193, `LikeSender.cs` L52).
- `Prompts.ConfigurationHash` wraps `video_context` and its version into the verdict version when enabled (L41). Plan 02 removes that.
- Notices: `RecoveryIssue(Platform, Item, Problem, Attempts, LastAttempt, RetryAfter, Recovery, Informational)` with `RecoveryDisposition` (`RecoveryQuery.cs` L7-20). `AttentionPanel` groups by disposition; `Reports.Status` prints `recovery <platform>/<item>: ...` lines (L54).
- `ffmpeg` shrinks vision images (`MediaFiles.cs` L70); `ffprobe` runs in `Maintenance.cs` L110.

## Target design

1. **Tool probe.** New `ToolHealth` in Infrastructure runs `yt-dlp --version`, `ffmpeg -version` and `ffprobe -version` with a 10 second timeout. It stores Kv `tools:<name>` as `{version|missing|error, at}`. The web host runs it at startup and the daily job repeats it. `Processing.Run` reads the Kv instead of spawning a missing tool.
2. **Notices.** `RecoveryQuery` emits an Attention issue when a feature that needs a tool is enabled and the tool is missing: `video_context.enabled` without yt-dlp; `llm.vision` without ffmpeg; video download fallback without yt-dlp as Informational. `status` prints one `tools:` line with versions.
3. **Feature failure streaks.** `VideoContext` keeps a Kv counter of consecutive failures by `Error`. Twenty consecutive `tool_missing`, `timeout` or `retrieval_failed` results raise an Attention issue "video context failing: <error>" and pause calls until the daily probe succeeds. The counter resets on success.
4. **Video context only where it adds information.** `VideoContextProvider` calls yt-dlp only for URLs outside facebook.com, fb.com, fbcdn.net, instagram.com and cdninstagram.com. Native platform videos get no metadata call and no cookies. The cookie copy path (L113-118) is removed from `VideoContext`.
5. **Download fallback without session replay by default.** `Maintenance.DownloadTool` runs yt-dlp without cookies. A new config key `media.video_fallback_cookies` (default false) allows cookies for permalink-only sources, and only when the platform is not in `NeedsRelogin` and no browser lock is held. The runbook explains the account risk.
6. **Pinned yt-dlp in the image.** Dockerfile `ARG YTDLP_VERSION` downloads the release binary from GitHub for that version, verifies its SHA-256, and installs it to `/usr/local/bin`. The version is bumped by a commit. `docs/16-deployment.md` 16.2 documents the same for direct Linux.
7. **Tests before publish.** The workflow gains a `test` job (restore, build, `dotnet test` with the Browser trait excluded). `publish` needs `test`. After the build, a smoke step runs `Feed.Cli --help` and `yt-dlp --version` inside the image. `latest` is tagged only when both pass.
8. **Honest enablement.** Manage settings show the tool status next to the video context toggle. Enabling the feature with a missing tool is allowed but shows the Attention issue at once.

## Contracts to preserve

- Binding rule 3: the web host never starts browser or model work. The probe spawns tools only to read a version string. If the reviewer prefers, the probe can run in the processing child instead.
- Video judgments keep using captions, preview images and extracted metadata where available (7.8). Native videos lose nothing they receive today, because the tool is missing anyway and the caption is already in the prompt.
- Binding rule 7 and the capture path are untouched.
- No new platform write. Cookies are never sent to a non-platform host.

## Implementation sequence

1. `ToolHealth` probe, Kv keys, `status` line, Manage notice. Tests with a fake PATH: missing tool yields `missing` and an Attention issue; present tool yields a version.
2. Failure streak counter and pause in `VideoContext`. Test with the fake tool: twenty failures raise the issue and stop spawns; one success resets.
3. Host filter in `VideoContextProvider` and removal of the cookie path there. Update `VideoContextTests` fixtures: a native Instagram URL makes no call; a YouTube URL does.
4. `DownloadTool` without cookies by default; `media.video_fallback_cookies` with the relogin and lock guards. Tests for both settings.
5. Dockerfile pinned binary with checksum. Build the image locally and run the smoke step.
6. Workflow `test` job, `needs`, smoke step. Verify on a branch push with `workflow_dispatch`.
7. Manage toggle status display, browser test.

## Spec updates

- `docs/07-media.md` 7.4 (fallback without cookies, the new key), 7.8 (external links only).
- `docs/09-llm.md` 9.10 (video evidence scope, `tool_missing` surfaced).
- `docs/16-deployment.md` 16.2 tool table (pinned yt-dlp, probe), 16.3, 16.10 (CI test gate, smoke step).
- `docs/04-instance.md` `video_context`, `media.video_fallback_cookies`.
- `docs/12-scheduler.md` 12.5 daily probe.
- `docs/13-cli.md` `status` tools line.
- `docs/15-ui.md` Manage tool status.
- `docs/17-testing.md` 17.1 (CI gate), video context regression section.
- `docs/RUNBOOK.md`: tools section, account-risk note for cookies.

## Verification

- Fast suite plus the tests above. The workflow run on a branch shows `test` before `publish`.
- Private instance: after deploying the new image, `status` shows a yt-dlp version, the Attention issue clears, and the cache gains `available` entries for shared external links only. No cookie copy appears under `video-context/private/`.

## Open questions for the reviewer

1. Should the version probe run in the web host or only in the processing child? Rule 3 reads as browser and model work; a version call is neither, but the reviewer decides.
2. Drop the cookie option for the download fallback entirely, or keep it behind the default-off key?
3. Twenty consecutive failures as the streak threshold, or time-based?
4. Should `video_context.enabled` default stay false, or become "auto" when yt-dlp is present?

## Out of scope

Verdict version contents (plan 02), frame sampling or transcription (`docs/VIDEO-UPGRADES.md`), the like-back cookie export after likes.
