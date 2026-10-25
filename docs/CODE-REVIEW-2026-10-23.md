# Code review, 2026-10-23

Full review of the repository at commit `252f9a1` plus the uncommitted navigation and parser work. Five reviewers covered the application layer, infrastructure and queries, the CLI, the web host, and the build, tests and deployment. The high findings were verified by a second read of the code. Instance-specific evidence (row counts from a live database) stays in `data/` per AGENTS.md.

## Verification state

| Check | Result |
|---|---|
| `dotnet build` | 0 warnings, 0 errors |
| Fast tests (non-Browser) | 374 run, all pass in 3 of 4 runs, 1 flaky failure |
| Browser tests | Not run, Playwright browsers absent in the review sandbox |
| Secrets or personal data in the repo | None found, fixtures are synthetic |

## Fix first

1. **Like-back can click a control that was never approved.** `Site.Heart` (src/Feed.Cli/Site.cs) returns `matches.Nth(i)`, a lazy page-wide role locator. `LikeSender` hovers and clicks it later. Playwright resolves the locator again at click time, so all scope, size and hit tests ran on the scan, not on the click. On a hydrating permalink, a newly inserted comment heart can take that index. On Facebook the Love option is also looked up page-wide after the hover. Pin the node and repeat the checks on that exact node right before the click. *Fixed 2026-10-24.*
2. **Feed-action and background POST routes accept cross-site requests.** The routes in src/Feed.Web/Program.cs for `/read`, thumbs, unhide, like, collect, background upload and remove have no antiforgery call, no Origin check and no cookie requirement. Only the Manage and person routes validate. Any page the owner visits can queue hearts, start collects, unhide muted posts or fill the disk with uploads. Add the antiforgery token to these forms and to actions.js, or reject non-GET requests whose `Sec-Fetch-Site` is `cross-site`.
3. **No Host allowlist.** Nothing restricts the Host header. A DNS-rebinding page can read the feed, media, debug log and Manage page, and take the antiforgery token from there. Configure host filtering for the configured host, localhost and loopback.
4. **Image source keys change on every capture.** `PayloadParser.SourceKey` strips a fixed denylist of CDN parameters, and `Ingest.ContentHash` includes that key. Meta also rotates `_nc_gid`, `efg`, `cb`, `ig_cache_key` and others. Each recapture of an unchanged post bumps its content revision, resets its verdict and summary, and downloads the image again. Videos take the same path. Build the key from the path plus an allowlist of rendition parameters, and migrate stored keys once. *Fixed 2026-10-24.*
5. **Facebook `/people/` URLs merge different authors.** `Identity.NonProfiles` (src/Feed.Core/Domain/Identity.cs) does not contain `people`. A URL like `/people/Name/1000123/` becomes the key `fb:people`, and `IdentityStore.Resolve` merges every later such person into the first one. Add `people` (and `pg`, `photo.php`, `l.php`) and take the id from the third segment.
6. **Checkpoint phrases are matched against the whole page.** `Site.Checkpoint` lowercases the full DOM and searches for phrases such as "this was me" or "two-factor". A caption or comment with those words sets the re-login flag and stops all scheduled work. Match only the URL, title or a challenge container.

## Medium

- **Scheduler slots that cross midnight never fire.** src/Feed.Web/Scheduler.cs computes fire time on the current local date. A slot of 23:55 with 7 minutes of jitter is recomputed for the next day at 00:02. Slots inside the spring-forward gap are also skipped. Compare UTC instants and consider yesterday's slots.
- **One missing raw file stops every processing run.** `Ingest.Register` reads `info.Length`, and `Ingest.File` calls it before its `try`. The exception cancels the whole run, the row never gets an `AttemptedAt` stamp, and the next run fails again on the same row.
- **An empty `keyword:` bullet hides the whole feed.** `Preferences.Parse` accepts an empty value, and `Contains("")` is always true in `Filters`. Reject empty keyword, mute and show values.
- **A busy collect child exits 0 and its request is marked done.** src/Feed.Cli/Capture.cs turns `ResourceBusyException` into status "refused" with exit 0, so a raced manual or person collect is dropped and the slot key is consumed. Spec 12.8 expects exit 75.
- **Unknown CLI options are ignored.** src/Feed.Cli/Arguments.cs accepts any name. `storage clean-hidden --dryrun` deletes for real, and `like --postid 5` drains every pending heart. Validate option names per command.
- **Token-budget signal is lost through the fallback endpoint.** `TokenBudgetException` derives from `FormatException`, which the fallback filter in `ModelClient` catches. If the fallback fails differently, processing never escalates the budget and recovery marks the raw "attention" instead of "retry".
- **Process liveness compares boot time exactly.** `ProcessIdentity` derives start time from `/proc/stat` btime. An NTP step can move btime by a second, and `Recover` then abandons a live run. Store the raw start ticks instead.
- **Visit-model diagnostics are never pruned.** `VisitDiagnosis` writes a JSON file with the full base64 screenshot per attempt. Prune, Measure and the full download reset do not cover that folder, so profile screenshots survive a reset.
- **`novnc.sh stop` aborts on a CLI-written pid file.** `BrowserSession` writes the PID with no trailing newline, so `read -r` returns 1 under `set -e`. Xvfb stays running.
- **Manage loads every `Runs` row on each GET.** `Management` reads the full table including stats JSON, and management-status.js refetches the page every 15 seconds. Nothing ever deletes runs.
- **Uncommitted navigation work loses scroll position on ordinary Back and reload.** navigation.js sets manual restoration and overwrites the saved state with 0 at load. It also calls `replaceState` every animation frame, which Safari throttles. Read the saved state before replacing it, and debounce the save.
- **Uncommitted `HasIssues` adds a per-render query with no index.** `RecoveryQuery.HasIssues` runs a correlated subquery on `TimelineVisits` per candidate raw. When no issue is open the full evaluation runs on every feed render. Add an index on platform, author and time, or cache the flag.
- **Rare view can exceed the SQLite variable limit.** `FeedQuery` passes in-memory rare ids with `Contains`. If EF expands them to one parameter each, the view fails above roughly 32,000 rare ids. Compute rare membership in SQL.
- **Parser version bump 13 to 14 re-queues all blocked raws.** The uncommitted change adds `FacebookLikeRef` only. The version comment reserves bumps for changes that recover rejected snapshots. The new test uses inline JSON, and no sanitized fixture covers `feedback_target_with_context`.
- **Flaky test from a process-wide pool clear.** `TestInstance.DisposeAsync` calls `SqliteConnection.ClearAllPools()` while xUnit runs classes in parallel. This produced an `ObjectDisposedException` in `JudgmentRetryTests` during one run. Clear only the instance's own pool, or disable pooling in tests.
- **CI publishes `latest` without running tests.** The docker-publish workflow builds and pushes on every push to main. Add a test job and make publish depend on it.

## Low and hygiene

- `page.ContentAsync()` throws during navigation and nothing catches it in `Checkpoint` or the login poll.
- The staged browser profile keeps stale SQLite journal and WAL files after a killed run, which can corrupt the cookie store.
- `Env = { DISPLAY }` in `BrowserSession` replaces the whole Chromium environment, so proxy and locale variables are lost.
- Visit error counters are snapshotted before the previous page drains, so late errors blame the wrong visit.
- Cached `unresolved` proposals are reused on manual retries in `CaptureRecovery`, so a manual rerun can never get a fresh answer.
- An `llm` hide is not restored after a deterministic rule is removed, because the post still counts as judged.
- `Register` re-reads and hashes every raw body with a `.visits` sidecar on each run. All authors are loaded per post inside the write transaction in `Processing`.
- `counts.Failed++` in `Processing.Apply` races with `Interlocked` updates elsewhere.
- `Backgrounds.Scan` hashes every image file each minute.
- Readability rule: `.new-divider` has no surface, `.banner` is about 31 percent opaque, and `.hidden-card` opacity dims the glass below readable levels.
- Masonry moves cards into column containers, so keyboard and screen-reader order is no longer chronological.
- The feed header can show "just now ago" (`CollectionSummary`).
- `?message=` is rendered as a trusted notice from free query text (`ManagementRoutes`).
- Only stdout is teed to the CLI log; parser warnings and fatal traces go to stderr only.
- `cookies.txt` is written with the process umask rather than 0600.
- tools/sanitize.py keeps numeric ids under keys outside its list, keeps coordinates, and never rewrites dict keys.
- Dockerfile has no `HEALTHCHECK`, base images are pinned by tag only, and `COPY . .` precedes restore so the restore layer never caches.
- Seven browser tests hard-code the Debug build path and pick a port with a close-then-bind race.
- Filter precedence has no table test that crosses rule types with the always-show protection.
- Non-current media rows and their files are never pruned by retention; only hidden-post age and video age expire files.

## Strengths

Stale model results are blocked by content and visibility revisions. Recovery output can only cite evidence ids it was given. External processes use argument lists with no shell. File serving rejects traversal and symlinks. Settings writes are validated and atomic. Razor output is encoded everywhere. Every browser loop is bounded. The tests check real outcomes with synthetic data.
