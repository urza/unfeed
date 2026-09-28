# Verification measurements — 2026-09-27

Environment: Ubuntu 26.04 x64, .NET SDK 10.0.401/runtime 10.0.12, EF Core 10.0.12, Playwright 1.63 Chromium 1243. All accounts, posts, policies and model responses used below are synthetic. No platform write or owner login occurred.

## Completed checks

- Locked restore and Release publish through `./build.sh`: passed, both applications in `out/`.
- Fast suite: **68 passed**, approximately 2 seconds test duration (about 3 seconds total).
- Browser suite: **4 passed**, approximately 6 seconds test duration (6.83 seconds total).
- Browser coverage: static HTML with JavaScript disabled, navigation/forms/thumbs, escaped captions, CSS/module delivery, summary expansion, gallery wrap/keyboard/focus return, three/one-column layouts and phone overflow, finite/raised render caps, headed staged profile and cookie export, repeated headed sessions, Instagram comment/post heart distinction and ambiguity rejection.
- Processing: actual fake-HTTP overlap/refill with batch 1 and 4, mixed platform/judge/summary turns, shared parallel budget, fallback provenance and no 4xx fallback, cancellation observing active requests, stale-result protection, inline budgets and failure backoff. These establish dispatch behavior, not GPU saturation.
- Recovery: migration/upgrade, active-request uniqueness, stale claim/live worker handling, startup failures and restart delay, incomplete image recovery without a new browser, published-video recovery without a fetch, identity merge revisions, resumable coverage, signing-token stability and retention.
- Backgrounds: fake eight-market/eight-day responses deduplicate to eight downloads, persisted removals prevent return, restart respects success/backoff, local/cached pool and pins coexist, ETag changes and escaping symlinks are excluded. No real Bing request was needed.
- Published app in a disposable instance: `init`, `rules`, bounded `process`, quiet `status`; HTTP health/feed/debug/gallery; real scheduler child claim/registration/completion all passed. CLI output persisted to its log while quiet stdout remained empty.
- noVNC helper: start, idempotent second start, HTTP 200 on `/vnc.html`, and stop all passed on a disposable display/port. No credentials entered.

## Render benchmark

Synthetic database: 420 chronologically ordered posts, varying text/summary presence, one four-image mosaic with explicit geometry; model/background downloads disabled and stacking off. Seven warm loopback HTTP requests, Debug build. Headless local Chromium, no network throttling. Paint excludes remote imagery and is not a real-phone measurement.

| Workload | Server timing | HTML bytes | Browser result |
|---|---:|---:|---|
| Default cap 300 | warm median 15.30 ms; max of 7 samples 28.36 ms | 490,445 | 1280×900: FCP 164 ms; DOMContentLoaded 237.6 ms |
| Cap 1000, 420 matches | 22.24 ms (one sample) | 685,325 | 390×844: FCP 164 ms; DOMContentLoaded 201.7 ms; no horizontal overflow |

The raised cap rendered every one of the 420 matching posts, with no hidden 300-row limit. These figures are not a claim about a 100,000-post page, large media files, a slow phone, or large retained database history. Read projections still load stored post facts for exact membership; rare windows now use sorted per-author dates and binary bounds rather than a full scan for each post.

## Repeating verification

The figures above are a historical synthetic benchmark, not a claim about the current build or a deployment host. Run chapter17's tests and record the revision, environment and workload when measuring again. Keep measurements involving real accounts, endpoint capacity or private datasets under data/; only publish sanitized synthetic results.

Direct-host tests do not validate Docker. A deployment check also covers non-root data-directory permissions, health/feed/debug routes, noVNC, headed Chromium and its host sandbox support. Rebuild the image after source changes.

Synthetic captures test known payload shapes, not ongoing platform compatibility. Live audience imports remain add-only until adapters provide authoritative completeness evidence. Each deployment validates login, the desired collection modes, processing, media and restart recovery; intended-post reactions are tested only when opted in.
