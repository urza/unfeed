# 14. Web host

You are here: the HTTP surface. Chapter 12 put the scheduler inside the web host. This chapter specifies routes, read models and static server rendering with Razor Components. Chapter 15 describes what the owner sees. Plain JavaScript and CSS enhance the HTML served by ASP.NET Core minimal API routes.

Source map: [Web routes](../src/Feed.Web/Program.cs) · [FeedQuery.cs](../src/Feed.Core/Queries/FeedQuery.cs) · [Backgrounds.cs](../src/Feed.Web/Backgrounds.cs) · [WebTests](../tests/Feed.Tests/WebTests.cs).

## 14.1 Shape

- One ASP.NET Core minimal API process. It hosts the scheduler (chapter 12), serves the page, the actions, the media files and the debug page.
- It binds `http://<web.host>:<web.port>` (127.0.0.1:8000) unless `--urls` or `ASPNETCORE_URLS` says otherwise. No HTTPS and no authentication. The default bind to the loopback interface is the protection. Remote access requires deployment-provided private networking or an authenticating proxy; the application does not supply authentication.
- Response compression is on. Static assets are served with a one-day cache and a content stamp that changes when the deployed CSS/JavaScript asset tree changes, so a deploy takes effect without a hard refresh.
- The three instance files reload before the next request or scheduler tick after a change, using one validated snapshot per request/tick (chapter 4). The host, port and log level are read once.
- The page render reads and never writes. Action routes perform the bounded database/file changes specified below; the hosted scheduler also writes its documented bookkeeping and maintenance state. Browser, scoring and post-media downloads run in CLI children. A hosted background task downloads Bing images and maintains the local pool (14.6); page and image GET routes never perform an upstream fetch. A dedicated service class is a reference organization, not a requirement; background failures remain isolated from page handling.
- Every page sets an `X-Render-Ms` response header. The performance budget is chapter 5.
- The process logs to `logs/feed-web.log` and to the console (chapter 16).

## 14.2 Rendering and browser enhancements

Razor Components render complete HTML on the server from the read models below. The .NET build compiles the components. Components consume prepared read data and do not query stores or perform mutations during rendering. Reusable page, toolbar, card, stack and debug components are a reference decomposition; component boundaries and internal DTO shapes may differ while exposing the same information and behavior.

- Static rendering only: no interactive Blazor server circuit, WebAssembly runtime, hydration or client-side application router. Navigation uses ordinary links. The HTML is useful before scripts load.
- Every action is an ordinary HTML form using the specified POST route and redirect. Native details/summary elements provide dropdowns and expandable content where suitable. Text, navigation, full-text/stack expansion, media links and form actions work with JavaScript disabled.
- Plain JavaScript modules enhance masonry layout, scroll anchoring, the lightbox, in-place thumbs updates and heart polling. Organize these by responsibility, use native browser APIs and load the entry module with `type="module"`. No frontend package manager, framework, bundler or transpiler is required.
- Modules use stable data attributes and the existing action/status routes. The backend owns selection, visibility, categories and action validation; the browser does not duplicate those rules or maintain another feed store.
- Plain CSS files use custom properties for the chapter 15 design tokens, media queries for responsive behavior, and ordinary selectors. Assets are local; there is no CDN dependency for frontend code or styles.
- Every stylesheet/script URL and transitive module import identifies compatible deployed content, so a deploy cannot mix old modules with a new entry script. The implementation hashes the asset tree at startup and serves it under a stamped URL prefix, so relative module imports carry the same version. Content-fingerprinted assets with correctly rewritten imports, or an equivalent coherent versioning scheme, are allowed without adding a JavaScript toolchain. Versioning only the entry script is insufficient.

Every browser-requested application mutation remains an HTTP POST to an action route; the host never starts a process from a request. Browser enhancements preserve the reader's position as specified in chapter 15.

## 14.3 Routes

| Route | Reads or writes | Answer |
|---|---|---|
| `GET /` | the feed. Query: `view` (a view key; empty means `ui.default_view`, which falls back to `all`), `platform` (a platform key), `hidden=1` (the hidden inspection mode), `unsorted=1` (the unsorted mode; `hidden` wins when both are set) | the page. An unknown view redirects to `/`. An unknown platform redirects to `/`. "Known platform" means a platform with stored posts. |
| `GET /debug` | the operator page model | the page, with `X-Render-Ms` |
| `GET /debug/log?file=cli|web` | the last 5000 lines of one log, or both merged by timestamp when `file` is absent | plain text, oldest first |
| `GET /healthz` | opens the database and performs a small read against the expected schema; no full-table count is required | `ok` plus a newline; a database failure is a 500 |
| `GET /media/{path}` | a stored media file. `path` is relative to the media folder. A path that escapes it, or names a file outside `media/`, is a 404. Never serve the database, the config, the profiles or the raws through this route. | the file, with range support, content type from the extension, `Cache-Control: public, max-age=31536000, immutable` |
| `GET /backgrounds/{name}` | one named local/cached background from the pool (14.6); never an upstream request | image bytes with content type and correct cache validation/versioning; matching ETag answers 304; unknown/removed name 404. Mutable names revalidate; immutable content-versioned names may cache longer. |
| `GET /debug/backgrounds` | current pool, selection/pin, blur setting and last download outcome | gallery page; no downloads or file mutations |
| `POST /debug/backgrounds/upload` | multipart files under `images`; 1–10 files, 10 MiB each, 20 MiB total, 22 MiB request cap including multipart overhead; supported extension and matching positive-dimension image headers required | save with unique safe names under instance `backgrounds/local/`, refresh pool, redirect with success feedback; invalid input returns gallery with HTTP 400 (server-level request rejection may return 413) |
| `POST /debug/backgrounds/pin` | validates form field `name` against the current pool and atomically writes pinned.txt | redirect to gallery; unknown name 404 |
| `POST /debug/backgrounds/unpin` | clears the pin and returns to rotation | redirect to gallery |
| `POST /debug/backgrounds/next` | clears the pin, chooses another random pool member when possible and resets the rotation timer | redirect to gallery |
| `POST /debug/backgrounds/remove` | removes the selected pool image named by form field `name`, clears its pin/selection if needed, and remembers a removed Bing identity | redirect to gallery; unknown name 404 |
| static assets | the stylesheet, the script, the favicon | one-day cache |
| `POST /read` | writes the `Kv` row `last_visit` = now | redirect back |
| `POST /posts/{id}/thumbs/{up|down}?view=<key>&scope=<live|hidden|unsorted>` | writes one feedback row with the post's author, its verdict at this moment, the view key and the scope. Every press adds a row. It hides nothing. | redirect back; 404 for an unknown post or signal |
| `POST /posts/{id}/like` | validates, queues a `Likes` row, writes a like run request (chapter 11) | redirect back; 409 with the problem text when the post cannot be liked |
| `GET /posts/{id}/like` | the newest `Likes` row of the post | JSON `{"state": "none|pending|sent|failed", "error": "<text or null>"}`. The card polls it after a press. |
| `POST /posts/{id}/unhide` | calls the shared ClearHidden operation, including its VisibilityRevision increment (5.2) | redirect back |
| `POST /collect` | a home request for each enabled platform without a pending/claimed collect request or active collect run; processing runs do not block it. Insertion uses the unique active-request constraint. | redirect back |
| `POST /collect/{platform}/{mode}` | one request with the same collect-specific guard; mode aliases are accepted | redirect back; 404 "collect refused: <platform> has no mode <mode>" |

"Redirect back" means: to the referring page, path and query, when its host is this host; else to `/`. So a plain form submit lands where it started.

## 14.4 The feed page model

The host supplies the following consistent information for each request. One aggregate model is the reference shape; query count, DTO names, projections and correctly invalidated caches are implementation choices. Membership, counts and explanations use compatible data/configuration snapshots, and caches cannot silently change the documented freshness or visibility rules. In words:

- **Scope.** The current view key, the current platform (or none), the hidden flag, the unsorted flag, the unsorted mode's availability (the model is enabled), the base query string that returns to the live feed in the same scope.
- **Navigation.** The views in taxonomy order plus `all` last, each with key, label, active flag and link. The platforms with stored posts, alphabetical, each with key, label, active flag, a dimmed flag for a paused platform that is the current one, and a link. A paused platform is otherwise omitted.
- **Counts.** The visible post count of the live selection for the view and platform scope, not capped. The friend count in scope. The dropped-ad count counts hidden sponsored/suggested posts, not every flagged post; an instance may allow them. Hidden and current-content-unsorted counts in scope. The newest finished collect, with its age and inserted count; when ingest was deferred, show that state rather than implying zero posts were found. Processing counts are separate. These stats describe the live selection even in inspection modes.
- **Collect state.** The main label (`Collect`, `Collect requested ✓`, `Collecting…`) and its short form, whether "collect now" is disabled and why, and one target per enabled platform and mode with its action URL, label, disabled flag and title. Per platform: an active collect run or collect child gives `Collecting <Platform>…` disabled; a pending request or an unregistered claim within its startup grace gives the label plus ` · requested ✓` disabled; a re-login flag gives disabled with "needs re-login"; else enabled, with the title `cooldown: the run starts in N min` while the cooldown runs. The states use durable records, so they survive a host restart. A process worker does not disable Collect; like actions follow browser ownership, so collection ingest does not block a heart.
- **Re-login.** The platforms whose flag is set. This blocks browser work, not processing of captured posts.
- **Processing.** The feed receives judgment counts and recovery issues for its Debug indicator. Detailed stage state, pending judgments/summaries/videos, retry eligibility and errors appear in Debug. Use the durable worker runs; do not replace last-collect freshness with last-processing time.
- **Coverage.** Last successful timeline check per target, not-recently-checked count, incomplete/failed visits, and sweep attempted/total/remaining counts. Unknown coverage stays unknown. A rendered page alone is not a successful capture.
- **Items.** The units in feed order after folding (chapter 10): each unit is a lead card, the folded cards behind it (empty when it is not a stack), the stack badge text, and the lead's date.
- **The divider.** In live mode only, when a last visit exists and the fresh units are more than zero and fewer than all: the id of the last fresh unit and the divider text. A unit is fresh when its lead is newer than the last visit. The text names the age of the oldest fresh post.
- **Empty state.** A message, optional links (label and href), and whether the "collect now" button is offered. The messages are in chapter 15.
- **Background.** The chosen public file URL `/backgrounds/<name>`, or the built-in gradient when the pool is empty; configured blur radius. Read an already published pool/selection snapshot. A pin or Next action appears on the next load without waiting for a cached generic image URL to expire.

## 14.5 The card model

Per post:

- the id and the platform key;
- the author's name, profile URL, avatar URL and initial;
- the relative time and the ISO time;
- the four flags: sponsored, suggested, reel, event;
- the summary only when its content revision matches;
- the "with" people, each with name and URL;
- for a null AuthorId, the observed author name and URL as display fallbacks, without author-derived privileges;
- the caption, with links and tagged people resolved;
- the shared author, URL and text;
- the memory label and text;
- the media list in post order, each with URL, width, height, a video flag and, for a video, its poster;
- the three tile indexes and the index the "+N" scrim opens at (chapter 15);
- the count of pruned media;
- the permalink;
- whether the heart is offered: the platform supports like-back, the instance has it on, the post has a like handle and a permalink;
- the like state: none, pending, sent, failed; and for a failed like its error text, so the tooltip can carry the reason after a reload;
- the thumbs up and down counts;
- the hide owner and reason;
- current-content category keys and score, or a pending/stale-content indicator; older output remains available only as explicitly labeled debug provenance;
- the "why here" text;
- the decision trail lines (chapter 10).

Page-level inputs to a card: its order, the hidden mode flag, the view key and the scope for the thumbs action, and the stack label.

Text rendering rules for the Razor components and their shared helpers:

- HTML-escape everything;
- turn bare `http(s)` URLs into links that open in a new tab, in the caption and in the summary;
- turn tagged names in the caption into profile links (https only) or bold spans.

Relative time: no date gives `-`; under 90 seconds `just now`; under 90 minutes `N min`; under 36 hours `N h`; under 14 days `N d`; else the date as `dd MMM yyyy`.

## 14.6 Backgrounds

**One pool.** Supported images in `backgrounds/local/` (`.jpg`, `.jpeg`, `.png`, `.webp`) and all retained downloaded Bing images in `backgrounds/` form one pool. A nonempty local folder does not replace Bing. `backgrounds.source` controls downloading, not whether existing cached or local files can be selected. Choose an unpinned random member for one hour, selecting a different member at rotation when at least two exist. Startup/periodic background scans with an immutable pool snapshot are the reference implementation; reconciled file notifications or equivalent cache maintenance are allowed. With no usable image, use the built-in SVG gradient (1920 by 1080, `#0d1b2a` to `#1b263b` at 55% to `#415a77`). A missing file falls back cleanly on the next snapshot/load; it never breaks the feed.

**Pin and gallery.** `/debug/backgrounds` shows both sources in one thumbnail gallery, marking the current and pinned image. Pin writes its public name to `backgrounds/pinned.txt` with a temporary file and atomic rename. It survives restart and overrides rotation while the image exists. Unpin resumes rotation; Next clears any pin and immediately chooses a different random image when possible. Remove deletes only that image's file in the instance background pool, never any original file outside it. Removing the pinned/current image clears that selection and selects another or the gradient. Unknown names and traversal attempts cannot address arbitrary files. Missing/invalid pins are ignored and reported in the gallery; background maintenance can clear them off the request path. The gallery exposes these actions as ordinary POST forms that work without JavaScript. Upload validation checks the entire batch before saving any files. JPG/JPEG, PNG and WebP are accepted; matching headers are checked rather than fully decoding image pixels. Publish complete files with temporary files and rename; client filenames cannot choose paths or overwrite an existing file. A storage failure may leave already published members of a batch. No database schema is needed.

Use stable, unambiguous public names and map them only to allowed pool images. The reference naming scheme is `local-<file>` for a local image and `bing-<hash of canonical image identity>.jpg` for a download. Other collision-free schemes are allowed when existing pins and URLs remain resolvable or receive an explicit compatible migration. Serve only supported image files whose resolved paths remain inside their background directory; pin/metadata files and escaping symlinks are not assets. The page names the selected file directly, so a changed pin produces a changed URL on its next load. Do not render the old generic `/background.jpg` URL or its ten-minute cache. If retained for compatibility, that route only redirects to the current asset with `Cache-Control: no-store` and never fetches Bing. Mutable local names must revalidate replacement bytes; content ETags with `public, max-age=0, must-revalidate` are the reference policy. Content-versioned immutable names may instead use long caching without hiding a replacement or pin change.

**Download off the request path.** When `backgrounds.source` is `bing`, hosted background work performs one archive refresh per UTC day, including on startup if that day has not been attempted. It is separate from browser/model dispatch and controlled by backgrounds.source, not scheduler.enabled. Read `https://www.bing.com/HPImageArchive.aspx?format=js&idx=0&n=8&mkt=<market>` for each of eight markets. The reference set is `en-US`, `en-GB`, `de-DE`, `fr-FR`, `it-IT`, `es-ES`, `ja-JP`, `en-IN`; market membership is an adapter choice that may change with evidence about availability and distinct images. These eight-day windows overlap across markets; deduplicate by canonical image identity before downloading and against files already retained. Strip the regional suffix (such as `_EN-US1234567890`) from Bing identities. Background scans consolidate byte-identical cached Bing copies, keeping the pinned filename first, then the selected filename, then a stable name. Remember discarded filenames to avoid downloading those copies again. Local files remain untouched. Content hashes also prevent removed photos from returning under another identity; this is exact-content matching, not perceptual matching of differently encoded images. Download `https://www.bing.com<urlbase>_1920x1080.jpg`, with an 8 second timeout per request. Validate returned origins/paths, accept image content only, and publish complete files atomically. Keep existing images when a market, image or the entire refresh fails, report the outcome, and retry failed refresh work no sooner than 10 minutes later. Persist refresh state under backgrounds so restarting does not repeat successful downloads or bypass failure delay. The metadata format and background task/class organization are implementation choices.

Use 1920×1080 assets, not UHD: the background blur makes the extra size unnecessary. Do not fetch Unsplash or Wikimedia, and do not filter Bing titles by keywords; the owner chooses removals in the gallery. Persist removed Bing identities so a daily archive refresh does not restore an image the owner removed; `backgrounds/removed.txt` is the reference storage, while another compatible private metadata representation is allowed. The pin remains the documented `backgrounds/pinned.txt`. Coordinate publication and gallery mutations so pin/removal races cannot resurrect a removed image or publish partial state. A lock around publication is one implementation; do not hold it through the entire network download. No request starts or awaits download work. The archive is undocumented; a change or outage leaves the local pool/gradient usable.

Apply `backgrounds.blur_px` (default 4) to the separate image layer, with viewport overhang as in 15.1. The background never delays a page for network work and background failures never fail the feed request. Report image identity/source, pin, pool count, last refresh and errors in the gallery. Record observed download counts/sizes in measurements when available; the number of distinct daily images is not a fixed guarantee.

## 14.7 The debug page model

Read-only. It exists so the operator never needs shell access to answer "what is happening":

- The render time, an instance file error, the scheduler's last error, the last tick, the resolved CLI command, the home zone, the scheduler's enabled state.
- Live worker identities (pid, OS start time, kind, phase, platform, mode, started, request id), including recovered and standalone runs. Browser ownership and processing ownership are separate.
- The slots (platform, mode, slot, jittered fire time, fired today).
- The collect gates per platform (enabled or paused, state: blocked, running, requested, cooldown, open; and a detail).
- The newest 15 runs (id, kind, phase, platform, mode, trigger, status, started, finished, found, new, error).
- Processing backlog per platform/stage, oldest pending age, next eligible retry and current owner; active model HTTP requests and prepared/unapplied queue counts.
- Current background, pin and pool count, with a link to `/debug/backgrounds`; last download outcome and errors.
- Coverage by target and active sweep cycle, including no visit, stale visit, rendered-without-payload and interrupted states.
- The effective rules projection also exposed by `rules`: type blocks, audience/tag exception, shield bypasses, resolved and ambiguous people, category definitions and view clauses. Personal values are read from the instance at request time.
- The close-friend timelines (name, URL, platform, last visit, last status, failures in a row, what the page said, the screenshot, the troubled flag).
- The merged log tail, the newest 80 lines.
- The newest 10 run requests, with claimed time, registered pid and OS start time, exit code, waiting reason and recovery outcome. Show recovered live children even when this host did not launch them.
- Per platform: total, visible and hidden posts, authors, friends. The thumbs counts.
- The like queue: counts per state and the newest 10 rows.
- The platform states (re-login flag, last ok run, last run finished).
- The live config in lines: per platform the enabled state, like-back, the close-friend count and the slots; the model; the scheduler; the retention days; the render cap and default view; the disk line; the data directory.
- The lock files (name, content, age).
- Every `Kv` row.

When capture saves a timeline screenshot, it also atomically publishes a copy under `media/diagnostics/<platform>/<run dir>/` and records that path in TimelineVisits.Screenshot. Serve that copy through the existing media route; raw directories remain outside the route. Copies use unique run paths and the raw-retention lifetime, not post-media retention. After expiry the debug page shows that the screenshot is unavailable.

Recovery projections additionally expose affected post/raw ids, task, error or upstream warning, failure count, attempt time, next eligible time and required action. The feed links a compact issues banner to `/debug#recovery`; the debug page and status command show details. Rendering remains read-only. Historical coverage and recovery state are separate: a reparse never forges a new successful visit.
