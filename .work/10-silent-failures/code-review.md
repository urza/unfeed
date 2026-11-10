# Round 1 (2026-11-10)

Target: `git diff 9b8a2a1...HEAD` (branch `10-silent-failures`), spec `docs/plans/10-silent-failures.md`

Reviewers: neutral, minimalist, security (reason: a downloaded binary enters the image, shell commands, cookie handling, a CI change), ux (reason: a status line, Manage text, recovery rows, nine doc changes). Performance was left out: the diff adds two short version calls per `process` run and removes spawns; nothing grows.

The owner is away. The session acts as owner proxy.

## Step back
Verdict: GO

Why: The heavy parts are deletions: the cookie replay is gone, and video context now covers only YouTube and Vimeo. The prune fix, the pinned yt-dlp with its hash, the image smoke check and the CI gate each fix a real, observed problem cheaply. The probe partly overlaps the Dockerfile smoke check. Still, it is the only signal for direct-Linux hosts and for the unexplained live case (256 entries, all `tool_missing`). It is one 61-line class plus one recovery block, so its cost is low. Leaving it out would make waiting video rows silent again. Merge as it is.

User (owner proxy, 2026-11-10): merge after the final checks pass.

## Findings

### 1. Chapter 6 still said the cookies are exported for the video downloader: Major, FIX
Reviewers: ux. Where: `docs/06-collectors.md` L25
Finding: After the change no process reads `cookies.txt`, but the collectors chapter still named the video downloader as its reader, so an owner would expect a login to fix failed video fallbacks.
Resolution: The line says that no process reads the file since plan 10 and that the export stays for a later use.

### 2. The fallback gate blocks on `error` too, and the row text said "not installed": Nit, FIX
Reviewers: neutral, ux. Where: `ToolHealth.Available`; `Maintenance.cs` (`Videos` catch)
Finding: `Available` is false for `error` as well as `missing`; the plan said "missing". A broken or slow yt-dlp then wrote "yt-dlp is not installed" on the media row while the recovery row said "did not answer a version call".
Resolution: The gate stays on both states (a tool that cannot answer a version call cannot download either; the next run probes again). The row text is "yt-dlp is not available (see the tools line); the row waits for the tool". The plan line now says `missing` or `error`.

### 3. The fallback gate was written twice: Nit, FIX
Reviewers: minimalist. Where: `Maintenance.cs` (`Videos`)
Finding: Two copies of `fallbackReady ? await DownloadTool(...) : throw new Win32Exception(...)` had to change together.
Resolution: One local `Fallback()` inside the loop body, with a comment that the probe's verdict and a failed spawn end in the same catch.

### 4. Leftover braces in `DownloadTool`: Nit, FIX
Reviewers: neutral, minimalist. Where: `Maintenance.DownloadTool`
Finding: The `try`/`finally` that deleted the cookie copy was gone, but its block stayed.
Resolution: The block is gone; the body sits one level out.

### 5. Dead cookie setup in the cancellation test: Nit, FIX
Reviewers: neutral, minimalist. Where: `VideoContextTests.CancellationPropagatesAndWritesNoCacheEntry`
Finding: The test still wrote `profiles/facebook/cookies.txt`, which nothing reads.
Resolution: The setup is gone.

### 6. A test-only parameter on `ToolHealth.Probe`: Nit, FIX
Reviewers: minimalist. Where: `ToolHealth.Probe`; `ToolHealthTests`
Finding: Only the test passed `params string[] tools`; production always probes `Tools`.
Resolution: `Probe(db, ct)` loops over `Tools`. The test stores an `error` state with `db.Put` and asserts `Available` is false and the `Line` text.

### 7. The recovery texts did not name the button, and "processing child" is jargon: Nit, FIX
Reviewers: ux. Where: `RecoveryQuery.cs` tool rows; `RecoveryPresentation.cs` `tools` explanation
Finding: A `process` run starts only when work is due, so on a quiet instance the row could stay after the install; the panel's "Process pending work" button starts a run and re-probes, but the text did not say so.
Resolution: Both texts name the button; the explanation says "Each processing run checks".

### 8. The docs said "scheduled run", but every `process` run probes: Nit, FIX
Reviewers: ux. Where: `docs/12-scheduler.md`, `13-cli.md`, `15-ui.md`, `16-deployment.md`, `09-llm.md`, `RUNBOOK.md`; the comment in `Processing.Run`
Finding: The guard is `scope.Kind == "process"`, which covers the scheduled run, the Manage button and `feed process`.
Resolution: The texts say "`process` run (scheduled, from the Manage button or from the CLI)".

### 9. A stale precedence sentence in 7.8: Nit, FIX
Reviewers: ux. Where: `docs/07-media.md` 7.8
Finding: "a recognized shared destination takes precedence over the native post" described a branch that no longer exists.
Resolution: The sentence is gone.

### 10. Security: All good
Reviewers: security. Checked: the pinned binary and its repo-held hash with `sha256sum -c` before `chmod`; the `test` job has no `pull_request` trigger and no token beyond `contents: read`; `ArgumentList` and the new `--` before the permalink; cookies no longer reach yt-dlp; `Source` accepts YouTube and Vimeo only through the existing `Web()` check; the probe runs fixed names with fixed arguments and cuts its stored text to 80 characters; Razor encodes the tools line and the rows; no new endpoint. A cookie copy left under `video-context/private/` by a crash before this change stays there (mode 0700, owner `app`); the owner can remove the folder by hand.
