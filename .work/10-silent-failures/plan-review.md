# Round 1 (2026-11-10)

Target: `docs/plans/10-silent-failures.md`

Reviewers: neutral, minimalist, skeptic, security (reason: the owner's cookies go to a second client, a downloaded binary enters the image, shell commands and a CI change), ux (reason: a status line, Manage notices, a CLI line, runbook text). Performance was left out: the plan removes spawns and adds two short version calls per processing run; nothing grows.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: CHANGE COURSE

Why: The real gains are cheap deletions and build guards: no cookie replay (items 4 and 5), the prune fix, the pinned yt-dlp with a Dockerfile smoke RUN, and the CI test gate. The probe, the Kv keys, the "tools" pseudo-platform in RecoveryQuery, the Manage line and the in-run guard all serve a feature that is off in the live config. After item 4 that feature reaches only about 72 posts with a YouTube or Vimeo SharedUrl (local feed.db). The smoke RUN already guarantees that the image has the tools.

Instead: Ship items 4 to 7. For item 1, keep only the prune fix: on Win32Exception, do not prune and do not count an attempt. Drop the probe, the notices, the status and Manage lines, and the guard. Add a probe later, only if the owner enables video_context on a direct Linux host and a missing tool goes unseen again.

User (owner proxy, 2026-11-10): accepted with one adjustment. The in-run guard is dropped. The probe, the two Kv keys, the notices and the `status` and Manage lines stay. Reasons: the owner named "a missing tool must become visible in status and on Manage" as the first priority of this plan, before the pin and the gate; the prune fix alone turns a pruned row into a row that waits forever with no signal, which is the silent failure the plan exists to end; the download fallback needs yt-dlp whether or not `video_context` is enabled, and a direct Linux host has no smoke RUN. The cost is one class of about 30 lines, two version calls per processing run, and one `tools` value in the recovery rows.

## Skeptic's cons

1. **The plan builds a probe, notices and a streak counter for a feature it also cuts to an unknown size.** (Target design 4 and the evidence table.) Item 4 removes yt-dlp for all native Facebook and Instagram videos, which includes 3,321 of the 3,847 video posts. The plan gives no count of shared external links. Price: the owner maintains three new failure mechanisms for a feature that may touch a few dozen posts. The plan does not ask whether to delete the feature.
   Answer: Agree in part: the streak counter, its pause and its notice are gone. Disagree on the rest: the probe and the notices also serve the download fallback and vision, not only the video context, and the feature is off by default (`Configuration.cs` L87), so it costs nothing while the owner does not enable it.
   User (owner proxy, 2026-11-10): keep the feature and the probe; the streak counter is dropped (decision 3 in the plan).

2. **The streak pause ends on the wrong signal.** (Target design 3.) A `--version` probe passes when the failure is `timeout` or `retrieval_failed`. Calls then resume, fail twenty more times and pause again, every day. Price: a notice that clears and comes back with no cause shown.
   Answer: Agree: the Kv pause and the daily reset are gone, and so is the guard.
   User (owner proxy, 2026-11-10): accepted.

3. **The outcome promises "within one tick", but the probe runs only at startup and once a day.** (Outcome against Target design 1.) Price: the main promise is false for runtime breakage.
   Answer: Agree: the probe moves to the start of every processing run. The Outcome now says "after the next processing run".
   User (owner proxy, 2026-11-10): accepted.

4. **The pinned yt-dlp goes stale for the same reason as the apt version.** (Target design 6.) "Bumped by a commit" has no owner, no schedule and no alert. Price: in a few months the owner has the same old binary, plus a checksum to update by hand.
   Answer: Agree in part: a pin goes stale, and nothing in the plan updates it. The apt package is staler on day one (noble ships a 2024 build). The `tools:` line on Manage and in `status` shows the installed version, and the RUNBOOK gets the bump step. An automatic update would make the image unreproducible.
   User (owner proxy, 2026-11-10): keep the pin; the version is visible; no automatic update.

5. **A cookieless fallback for permalink-only Instagram and Facebook sources will probably fail every time.** (Target design 5 and Open question 2.) Price: a risky option to test and document while the decision stays open.
   Answer: Agree on the option: the key and the cookie lines are dropped. On the live copy, 0 of 4,158 video rows are permalink-only and the fallback ran 5 times in total, each against the missing tool, so no working path is lost. The cookieless fallback stays as the existing code minus six lines; it is the last resort for an expired CDN URL.
   User (owner proxy, 2026-11-10): drop the key and the cookies (decision 2 in the plan).

6. **One plan mixes four unrelated changes.** (Implementation sequence; nine spec updates.) Price: no part can ship or revert without the others.
   Answer: Disagree: the four parts close one gap that the Problem section states, a tool that is absent fails every item with no signal. The probe shows the absence, the pin and the Dockerfile smoke check prevent it, the test gate prevents a broken image, and the scope cut removes the calls that failed. Each is its own commit on the branch and reverts on its own.
   User (owner proxy, 2026-11-10): keep one plan.

7. **The plan hands its binding-rule question to the reviewer.** (Contracts; Open question 1.) Price: the next session decides the architecture first.
   Answer: Agree: decided now. The probe runs in the processing child; the web host spawns nothing (rules 3 and 8 in `docs/02-overview.md` L70, L75 untouched).
   User (owner proxy, 2026-11-10): decision 1 in the plan.

## Findings

### 1. A missing yt-dlp prunes videos for good: Major, FIX
Reviewers: neutral. Where: `Maintenance.cs` L80; plan item 1 and 5
Finding: `Videos` marks a row `PrunedAt` on the first `Win32Exception`, so every video that falls back to the tool while yt-dlp is absent is dropped forever. The live copy has 5 such rows.
Resolution: Item 1 now says: the fallback is skipped while the probe key says `missing`, no attempt is counted, and `Win32Exception` no longer prunes. `MediaTests` gets the case.

### 2. The streak counter has no exact rules, duplicates the probe, and raises a second row for one cause: Major, ESCALATE
Reviewers: neutral, minimalist, ux, skeptic. Where: plan item 3
Finding: `VideoContextProvider` has no database for a Kv counter; cached failures were not excluded; a `--version` probe cannot lift a `retrieval_failed` pause; with the scheduler off the pause never lifts; yt-dlp missing raised both "yt-dlp missing" and "video context failing: tool_missing". The minimalist proposes to cut the item.
Resolution: Question for the owner: cut the streak, or keep a smaller guard? Recommended: an in-run guard in `Processing.Run`, 5 consecutive fresh `timeout` or `retrieval_failed` results, no Kv, no notice, reset per run. It bounds a broken extractor to about 5 timeouts per run (the owner's "wasted work" case) and needs about ten lines.
User (owner proxy, 2026-11-10): first the in-run guard, as recommended; after the step-back verdict the guard is dropped as well. Item 3 now records why no streak mechanism exists.

### 3. The cookie key for the download fallback re-enables the session replay the plan calls a risk: Major, ESCALATE
Reviewers: minimalist, skeptic, neutral. Where: plan item 5; `Maintenance.cs` L64-72, L89-94
Finding: The key brings a second code path, two guards, tests for both settings and a runbook risk note. The neutral reviewer adds that the fallback also runs when a direct CDN URL fails (L70), not only for permalink-only rows, so cookies off changes that path too.
Resolution: Question for the owner: drop the key? Recommended yes. Measured on the live copy: 0 permalink-only video rows, 5 fallback attempts in total, all against the missing tool. No working download is lost. The expired-URL path is now named in the plan.
User (owner proxy, 2026-11-10): dropped. The fallback runs without cookies; no key.

### 4. The host filter duplicates `Source()` and leaves dead branches: Major, FIX
Reviewers: minimalist, neutral. Where: `VideoContext.cs` L79-97; plan item 4
Finding: `Source` already sorts URLs by host. A second filter in `Get` leaves the Facebook and Instagram branches, the native permalink branch and `CookiePlatform` as dead code; `fbcdn.net` never reaches the function.
Resolution: Item 4 is a deletion inside `Source` plus the removal of `CookiePlatform` and the cookie copy path.

### 5. The probe is wider than the evidence and runs in the wrong place: Major, FIX
Reviewers: minimalist, ux, skeptic. Where: plan items 1 and 2; open question 1
Finding: Tools in an image do not vanish between restarts, so a daily repeat finds nothing; a startup-only probe leaves a fixed tool "missing" for a day on direct Linux; ffprobe absence only leaves width and height null; the web host probe touched binding rules 3 and 8; nothing said what shows before the first probe.
Resolution: The probe runs at the start of every processing run, for yt-dlp and ffmpeg only, and the web host spawns nothing. Before the first run the line reads `tools: not checked yet` and no issue exists.

### 6. ffmpeg missing with vision on turns the dot red, but nothing breaks: Major, FIX
Reviewers: ux, neutral. Where: `MediaFiles.cs` L77-78; plan item 2
Finding: `Vision` falls back to the original image, so classification works. An Attention row would stay red for an owner who does not install ffmpeg.
Resolution: ffmpeg missing is Informational when vision is on, absent otherwise. yt-dlp missing is Attention only while `video_context.enabled` is true.

### 7. The Manage toggle does not exist: Major, ESCALATE
Reviewers: ux, minimalist. Where: `Manage.razor` L125; `Management.cs` L216-220; plan item 8
Finding: The AI processing form has "Enable AI classification" and "Include images" only; `video_context.enabled` is a config-file key. The ux reviewer proposes to add the toggle; the minimalist proposes to cut item 8 because the Attention row already shows on Manage.
Resolution: Question for the owner: add a toggle, or show only the status? Recommended: no toggle. The key stays in the config file as today; the AI processing panel shows the `tools:` line (same pattern as the backup line from plan 08), and the Attention row is the signal. The hint "applies to shared external links" goes into docs 04 next to the key.
User (owner proxy, 2026-11-10): no toggle; the line and the row.

### 8. The tool issues had no Recovery text and no platform value: Major, FIX
Reviewers: ux. Where: plan item 2; `Management.cs` L32; `RecoveryDetails.razor`
Finding: `Recovery` is the "what to do" text on Manage and in `status`; `PlatformName` returns "Instagram" for any value that is not `facebook`.
Resolution: `Platform = "tools"`, `Item` the tool name, one Recovery sentence per tool; `PlatformName`, `PlatformGlyph` and `RecoveryPresentation.Category` learn the value.

### 9. The CI verification cannot run on a branch, and the smoke step needs a build-run-push split: Major, FIX
Reviewers: neutral, minimalist. Where: `.github/workflows/docker-publish.yml`; plan item 7 and step 6
Finding: `publish` runs only on `main`, so a branch dispatch never shows it; the single `build-push-action` step pushes `latest` before any smoke test.
Resolution: The smoke check is a `RUN` in the Dockerfile, so the build fails by itself. The workflow gains the `test` job and `needs: test` only. The test job runs in the SDK container to avoid a new unpinned action. Verification is a YAML parse and a dry read; the first real run is the next push to `main`.

### 10. The download asset and the hash source were not named: Nit, FIX
Reviewers: security, neutral. Where: plan item 6
Finding: The `yt-dlp` asset is a zipapp that needs python3, which the aspnet image lacks once the apt package goes; a hash downloaded from the same release protects nothing.
Resolution: The asset is `yt-dlp_linux`; `ARG YTDLP_SHA256` lives in the repo, taken once from the release's `SHA2-256SUMS` (2026.10.26), and both ARGs change in one commit.

### 11. The reason for the missing yt-dlp on the live host is unknown: Nit, DECLINE
Reviewers: minimalist. Where: plan Problem section; Dockerfile L7
Finding: apt installs yt-dlp since the initial commit, yet the live cache shows only `tool_missing`; a direct install or an old image would not be fixed by the pin.
Resolution: Declined as a blocker. The cause cannot be read from the repository. The probe shows the installed version after the next deploy, and the pin is justified on its own by the age of the apt build. The plan states the unknown.

### 12. Wrong line references and a wrong test premise: Nit, FIX
Reviewers: neutral. Where: throughout
Finding: `VideoContextConfig` is at L86-91; `ExportCookiesOnClose` at `Capture.cs` L37 and L197; `Prompts.ConfigurationHash` at L45 (and L67); `Reports` recovery line at L55; the parser anchors moved to L460-461 and L536-543; no test fakes PATH, `VideoContextTests` uses `IVideoMetadataTool`.
Resolution: Anchors corrected; the test plan uses the existing fakes and a resolver for the probe.

### 13. Open question 4: Nit, FIX
Reviewers: minimalist. Where: open question 4
Finding: An "auto" mode adds a third config state nobody asked for.
Resolution: The default stays false (decision 4).

### 14. Security: All good besides finding 10
Reviewers: security. Checked: only YouTube and Vimeo reach yt-dlp after item 4, with no cookies; `ArgumentList` prevents option injection; the probe runs fixed binaries with fixed arguments and Razor escapes the version string; the subtitle fetch is unchanged; the workflow keeps `contents: read`, `packages: write` on `publish` only, and the `main` guard.

### 15. Verdict version: not a reviewer finding, recorded for the next session
`Prompts.ConfigurationHash` includes `video_context` and `VideoContextProvider.Version` when the feature is enabled (plan 02 kept that). This plan leaves `Version` at 1 and changes which posts get a source, not what an existing source returns, so no verdict goes stale. The live config has no `video_context` key, so the feature is off there. `docs/plans/README.md` row corrected.
