# 6. Collectors

You are here: the capture. Chapter 5 defined the tables. This chapter specifies how a post travels from the platform's page into the raw store and then into those tables. It covers the browser, the adapters, the capture protocol, the parsers, ingest, the friends import, session health and reparse. Media files are chapter 7. Everything here runs in the CLI process, never in the web host.

Source map: [Capture.cs](../src/Feed.Cli/Capture.cs) · [BrowserSession.cs](../src/Feed.Cli/BrowserSession.cs) · [Site.cs](../src/Feed.Cli/Site.cs) · [PayloadParser.cs](../src/Feed.Core/Infrastructure/PayloadParser.cs) · [Ingest.cs](../src/Feed.Core/Application/Ingest.cs).

## 6.1 The browser

- Playwright for .NET drives Chromium. One persistent browser context per platform, opened on that platform's profile directory.
- The browser is always headed. On a host without a display, the CLI starts a virtual X display (`Xvfb`) on `browser.display` and points `DISPLAY` at it. When `DISPLAY` is already set, or the OS has a desktop, nothing is started. A missing `Xvfb` is a loud failure. There is no headless fallback: the sites treat a headless browser differently.
- Viewport 1280 by 900. No user-agent override, no stealth flags, no extra launch arguments. Chromium presents itself as itself.
- The locale comes from `LC_ALL` or `LANG`. The timezone comes from `timezone` in config, then `TZ` in the `Area/City` form, then `/etc/timezone`, then the process zone. The scheduler and the browser share one clock by construction.
- The Chromium sandbox is on unless `browser.no_sandbox` or `FEED_NO_SANDBOX=1` turns it off.
- The session uses the first open page of the context or opens one.

## 6.2 Profile staging and profile preferences

The instance directory may sit on a slow mount with its own locking. With profile staging enabled (the default), Chromium opens a local staged copy. With staging disabled, the owner selects an in-place profile on a filesystem suitable for Chromium.

- Before launch, the stored profile `profiles/<platform>/` is copied to a local staging directory (`browser.stage_dir`, else `FEED_STAGE_DIR`, else the user cache directory plus `feed/profiles/<platform>`). The current implementation uses a non-deleting recursive copy and excludes cache and lock directories: `Cache`, `Code Cache`, `GPUCache`, the Dawn and shader caches, `Service Worker/CacheStorage` and `ScriptCache`, `Singleton*`, `lockfile`, `BrowserMetrics`, `Crashpad`, `component_crx_cache`, `optimization_guide_model_store`, `Safe Browsing`, `segmentation_platform`. Symlinks are skipped. Staging reduces cache copying; its size depends on the profile.
- Stale `Singleton*` files are removed from the directory Chromium will open. With staging off, only this cleanup runs and the stored profile opens in place.
- After the context closes, the staged profile is synced back the same way. A failed sync-back is a warning. Transient caches are excluded from sync-back. A cache cleanup change must preserve the stored session and successful cookie export.
- Before every launch, the staged profile's Chromium `Preferences` file gets a notification-permission block for `https://www.<host>:443` of every site host of the platform (`facebook.com`, `fb.com`; `instagram.com`), only where no decision exists yet. An existing allow or block stays. The write is atomic. For a fresh profile, create a minimal preferences file before the first launch so permission is decided from the beginning. The reason: the sites open a "turn on notifications" dialog only while the permission is undecided. The dialog has no "never", and it locks the page scroll, so a collector must clear it before scrolling.
- After navigation and during render/scroll polling, dismiss a recognized notification opt-in dialog through its unique visible "Not Now"/"Later" button (including supported Czech equivalents). Limit dismissal to the notification dialog; never click an unrelated button, enable notifications, or dismiss authentication/checkpoint dialogs. If a recognized prompt has no unambiguous dismissal or remains open, fail with a diagnostic for operator inspection rather than silently calling a blocked capture successful. Apply the same handling while waiting for login and reaction controls.
- After a collect, a friends import or a login, the session's cookies are exported in Netscape format to `profiles/<platform>/cookies.txt` for the video downloader. The fresh file must survive the profile sync-back. Write it after the sync-back, or exclude it from the sync.
- One profile is never opened by two processes. The per-platform lock (chapter 12) guarantees that.

An existing Chromium profile can be imported with `profile import`, which copies it into the instance with the caches excluded.

**Copy contract.** The current copy mechanism is an internal choice. An equivalent implementation preserves profile data and existing permissions, excludes transient caches/locks, holds the platform lock during its copy, never copies a profile open in a live browser, reports sync failures, and keeps the successful cookie export after sync-back. Staging and browser exclusivity remain required where configured; replacing the copy mechanism does not relax them.

## 6.3 Login

`login --platform X` takes the platform lock, opens the headed browser on the platform's home URL, and prints that the human logs in through the noVNC window, two-factor included. It polls every 2 seconds, for up to 10 minutes, until the URL is a home URL and a feed element is present. A checkpoint during the wait does not abort: the human clears it. On success the cookies are exported and the platform's re-login flag is cleared. A timeout exits with code 1. The session then lives in the profile and every later run reuses it.

## 6.4 The adapter

One adapter per platform. Three pieces of knowledge, kept apart:

**The registry** answers which platforms exist and in what order (`facebook`, then `instagram`), which ref prefix each uses (`fb`, `ig`), which collect modes each supports (all three for both), and whether each supports like-back (both). Enabled or paused comes from config, not from the registry.

**The site profile** holds the site facts:

| Fact | Facebook | Instagram |
|---|---|---|
| home URL | `https://www.facebook.com/` | `https://www.instagram.com/` |
| timeline URL | `https://www.facebook.com/<handle>/` | `https://www.instagram.com/<handle>/` |
| home paths | `/`, `/home.php` | `/` |
| feed rendered when one of these exists | `div[role='feed']`, `section[role='feed']`, `[aria-label='News feed']`, `div[role='main'] [role='article']`, `div[role='main'] h4` | `article`, `main article`, `[role='feed']`, `[data-testid='feed']` |
| timeline rendered when one of these exists | the feed selectors, or post/photo permalinks inside the main landmark (6.17) | `article`, `a[href*='/p/']`, `a[href*='/reel/']`, `a[href*='/tv/']` |
| checkpoint URL markers | `/checkpoint`, `/login`, `login/identify`, `two_factor` | `/accounts/two_factor_authentication`, `/challenge`, `/accounts/disabled`, `/accounts/login`, `/email-confirmation`, `/two_factor` |
| checkpoint page text markers | "unusual activity", "confirm your login", "confirm this was you", "two-factor" | "check your phone", "we sent you a code", "help us confirm", "account has been temporarily disabled", "this was me" |
| capture gate | POST to a path containing `/api/graphql/` whose `fb_api_req_friendly_name` (query string first, then the form body) contains `CometNewsFeed` or `Feed` | GET or POST, status 200, host `instagram.com` or a subdomain, content type JSON or plain text (also `text/javascript` for a named feed/profile GraphQL operation), path contains one of `/api/graphql`, `/api/v1/feed`, `/api/v1/user`, `/api/v1/users`, `/api/v1/friendships`, `/api/v1/media`, `/api/v1/discover`, `/api/v1/explore`, `/api/v1/clips`, `/graphql/query`, and none of `/ads/`, `/comment`, `/delete`, `/direct`, `/friendships/create`, `/friendships/destroy`, `/friendships/like`, `/friendships/show_many`, `/ig_sso_users`, `/like/`, `/logging`, `/media/like`, `/reels_tray`, `/stories/tray`, `/unlike/` |
| people-list gate | every GraphQL POST | GET, status 200, JSON or text, path contains `/api/v1/friendships/` and `/following/` |
| raw file suffix | `.jsonl` | `.json` |
| dedupe identical bodies | no | yes, by SHA-256 |
| images per post | 8 | 10 |

Instagram named GraphQL operations are captured only for `PolarisFeed*`, `PolarisProfilePosts*` and `PolarisProfileReels*`. Profile pages also preload inbox, encrypted-cookie, promotion, share-sheet and story-tray data on the same URL; these unrelated operations are excluded before their response bodies are saved. Unnamed legacy JSON/plain-text requests retain the endpoint gate above; unnamed JavaScript-typed responses are excluded. The parser treats accepted JavaScript-typed bodies as JSON and never executes them.

A timeline URL accepts a handle, `/handle` or `@handle` and yields `<timeline base><handle>/`. A full `http(s)://` URL is used unchanged.

**The parser hooks** include these functions:

- extract the post ids from a file, for the no-new stop;
- extract the posts from a file;
- extract the people from a file;
- tell whether a file is the site's empty-timeline answer;
- extract the embedded payloads from page HTML;
- navigate to the people list and scroll it.
- extract friends-list completion evidence: the authoritative list identity, pagination cursors and terminal marker, any exact total, and whether a page or person failed to parse (6.11).

A new platform needs registry/site/parser support and verification of its capture, identity, coverage and optional reaction behavior.

**Adapter evolution.** Registry/site/parser separation describes responsibilities, not mandatory classes. URLs, selectors and payload fields in this chapter are observed compatibility facts. Update or extend them from saved/live evidence when the site changes, keeping sanitized regressions for existing shapes. Collection remains browser-driven on the requested surfaces; accepting a new field or selector does not authorize additional writes or wider collection. Unknown shapes produce diagnostics, not guessed identities or completeness.

## 6.5 Mode surfaces

A mode names a surface to capture, not a filter to apply later.

| Mode | What it captures |
|---|---|
| `home` | the home feed, scrolled to the cap or the no-new stop, followed by any configured `home_timeline_authors` at timeline depth |
| `close_friends` | the home feed first, then the timelines of configured `home_timeline_authors` and resolved close friends, at timeline depth. Deduplicate authors across both lists. Both parts are one run. Only the home part decides `ok` or `capped`. |
| `all_followed` | no home feed. Resume the saved sweep cycle at its next pending target; visit at most `--friends-limit` profiles (default `platforms.<p>.sweep_limit`), at timeline depth. Report cycle progress separately from run success. |

- Close-friend entries come from `--friends` for this run, else from `platforms.<p>.close_friends`. They resolve through the people matcher against the friend list. An unmatched entry logs a warning and is skipped.
- Optional `platforms.<p>.home_timeline_authors` uses the same platform's imported friend/following list and identity matching. `rules` reports resolution; unmatched entries are skipped. Prefer exact refs to ambiguous names. These additional visits use normal timeline coverage and checkpoint handling. They do not change close-friend membership or bypass filters. `--friends` only overrides the close-friends portion; `--person` replaces the entire home-plus-timelines sequence.
- Timeline depth is 2 scrolls. `--scrolls` overrides it, clamped to 2 to 5. The same `--scrolls` value also replaces the feed scroll cap of the home or `--person` surface, without a clamp.
- `--person <handle>` replaces the home surface with one person's timeline, at the feed scroll cap. It is the per-person tuning tool. It skips the close-friend visits, records a timeline-visit row for coverage, and is ignored in `all_followed`.
- No platform's own close-friends feature is used. Facebook's list is stale and Instagram's tray is ephemeral. The mode means the instance's own list.
- An Instagram profile grid is not a feed. A timeline visit waits for grid links, never for the home feed's article selector, or it scrolls an empty page.

## 6.6 The capture protocol

One run, one platform, one mode, one process:

1. Register the scheduler claim and run identity before work (12.8), or create a standalone run with the same process identity. Refuse/defer a re-login gate as appropriate, acquire the browser lock, and recheck the gate under the lock. Set phase `browser` before staging the profile. No ingest or processing lock is held while browsing.
2. Establish which observed ids are already stored, and track distinct ids first seen in this run. Preloading a set is the reference implementation; indexed lookups or a bounded cache may provide the same no-new signal without loading the entire history.
3. Open the browser: display, staging, the notification block, launch.
4. Attach a response listener with the capture gate. Write each body to a temporary file, atomically rename it to `NNN<suffix>`, then register RawSnapshots before parsing any ids. Partial temporary files are never parsed. The ledger and empty-timeline detection read only saved bodies. Listener/write errors are counted and logged as warnings; they make coverage incomplete rather than disappearing into a success counter.
5. Navigate to the surface. Wait for the page to load (60 seconds, DOM content loaded). Check for a checkpoint. Wait for the feed to render (poll every 2 seconds, 60 seconds). A feed that never renders ends the run as `error` with "feed never appeared; session stale or page blocked".
6. Harvest the embedded payloads from the page HTML (Facebook renders the first feed units into the page; see 6.8). Each one is stored as a batch like a response.
7. Scroll: check for a checkpoint, scroll one wheel step of 900 px, pause 2 to 5 seconds at random, drain the ledger. For ordinary home and multi-person collection, an id not already stored or seen in this run resets the no-new streak; none adds one. Stop at the scroll cap, or when that streak reaches 3. For an explicit `--person` visit, stored posts do not justify an early stop: continue while the scroll position changes, document height increases, or additional distinct post ids appear in this visit (including stored posts). Only three consecutive steps with none of these signals stop early, with reason `stalled`. The scroll cap and checkpoint/explicit-empty stops still apply. A stall does not prove complete history.
8. For a timeline mode, visit each target (6.12), then pause 3 to 8 seconds.
9. On any checkpoint: stop at once, mark the run `checkpoint`, set the platform's re-login flag. No retry.
10. Drain in-flight response bodies for up to 30 seconds. On success, obtain cookies before closing the context. Close the browser, sync the profile back, atomically write the successful cookie export, and release the browser lock. Checkpoint/error skips cookie export but still closes and syncs. Set phase `ingest`; browser ownership is now free even though this collect process is alive.
11. Acquire the platform ingest lock and ingest the run directory (6.10), including deterministic filtering before each new post commits. This is attempted after every capture outcome. If the ingest lock is busy, leave snapshots pending instead of holding browser ownership or waiting indefinitely. If ingest fails, record the failure and leave its snapshots pending. Capture status and ingest status are reported separately; neither a later model failure nor deferred ingest erases the capture outcome.
12. Finalize capture outcome: clean home cap is `capped`, clean no-new stop is `ok`; a sweep reports whether its selected visit budget completed and whether the cycle has remaining targets. Never replace checkpoint/error with capped. Persist coverage and stop reasons. Do not call the model or download pending videos here; the independent worker finds due work even after a failed capture.
13. An `ok` or `capped` run clears the re-login flag. Finish the run row with the counts, the stats and the error, and print one summary line.

The table gives the named adapter defaults. Scope limits, explicit overrides, no-new stop rules, human-pacing ranges and checkpoint/no-retry behavior remain requirements. Wait/poll mechanics and timeout budgets are operational tuning points: an event-driven wait or an evidence-backed timeout change is allowed if it remains bounded and preserves the same success/failure evidence. Do not shorten human pauses or increase collection volume as an implementation optimization. New public configuration keys require a documented configuration contract.

| Constant | Value |
|---|---|
| feed scroll cap | 12 |
| timeline depth | 2, `--scrolls` clamps to 2 to 5 |
| no-new stop | 3 consecutive scrolls without an unseen id |
| scroll step | 900 px |
| scroll pause | 2 to 5 s |
| profile pause | 3 to 8 s |
| list scroll cap | 60, for the friends import |
| page load | 60 s |
| feed wait | 60 s, polled every 2 s |
| login wait | 10 min |
| in-flight drain | 30 s |
| viewport | 1280 by 900 |

The summary line: `collect <platform> <mode>: <status> run=#<id> (found=, new=, scrolls=, batches=, visited=, skipped=, empty=, media=); ingest=done|pending|failed; coverage=<summary> [error=...]`. The exit code is 1 on `error` or `checkpoint` or an unsupported mode, else 0.

## 6.7 The raw store

- Layout: `raw/<platform>/<run dir>/<NNN><suffix>`. The run directory is named from the UTC start time, `yyyyMMddTHHmmss`, with a prefix per kind: none for `home`, `close-friends-` for `close_friends`, `friends-timelines-` for `all_followed`, `friends-` for the friends import.
- Directory classification: a name that starts with `friends-` but not `friends-timelines-` is a friends-list capture. Every other directory is a feed capture. Reparse walks the feed captures.
- Timeline screenshots `timeline-<handle>.png` also live in the run directory. They have no snapshot row. Publish their diagnostic media copies and record the served path as specified in 14.7.
- Registering a file writes one RawSnapshots row immediately after its atomic rename, idempotent on relative path, with size, last-write time and capture run id. Recovery scans complete files missing registration after a crash. The active capture's directory is not ingested by another worker until its process has left phase `browser` or died; this avoids racing in-flight bodies.
- `Posts.RawRef` holds the relative path of the file the row was first parsed from.
- Parse failures never delete or invalidate a raw file. Retention (chapter 7) is the only deletion path.

## 6.8 Facebook

**The gate** keeps GraphQL POSTs whose friendly name contains `CometNewsFeed` or `Feed`.

**Embedded payloads.** Facebook renders the first units of a feed or a timeline into the page HTML as preloader payloads. A capture that reads only network responses misses the newest post of every visited profile and the top units of every home load. After the feed renders and before the first scroll, the capture scans every `<script type="application/json">` block. It walks each block for an array of the form `[string, object]`. The string must carry a feed marker. The object must hold a `__bbox.result` with a `data` object. Each such result is stored as a batch. Other preloaders are ignored. The log says `embedded: N preloaded batch(es)`.

**Story candidates.** A node is a story when its `__typename` contains `Story`, it carries a creation time or a sponsored marker, it has a post id (`post_id`, else `id`; a non-empty string, or a number above zero), and it has text, media or shared text.

**Grouped timeline posts.** Under `CometStoryAggregatedStoriesStrategy.story.interesting_substories.edges[].node`, a post can omit its typename and top-level creation time. Extract each child using its own post id, actors, message and permalink; take its timestamp from its own context-layout metadata. Never borrow the aggregate timestamp or author, and do not duplicate child media onto an otherwise empty aggregate shell.

**Fields.**

- Platform context: retain the story's own `comet_sections.context_layout.story.comet_sections.title.story.title.text` in `StoryTitle`, including photo-update context. Never borrow a shared/attached story's title or insert this platform-generated text into the author caption.
- Text: `message`, else the message under the comet content sections, else a flat `text`. An object yields its `.text`.
- Author: the first of `actors[]`. Name from `name`. URL from `wwwURL` or `url`, made absolute. Id from `id`, `idString` or `__dr`, else the numeric id from the URL, else the URL slug.
- Like ref: `feedback.id` or `feedback.targetID`.
- Posted at: `creation_time`.
- Permalink: `wwwURL`, `permalink`, `permalink_url` or `url`. A relative URL is prefixed with `https://www.facebook.com`. `http` becomes `https`. Anything else is dropped.
- Shared content from the root `attached_story`, the content-section story’s `attached_story`, or the attached story within `attachments`: author is the first actor's name, text is its message, URL is its `wwwURL` or `permalink_url`. Caption and shared text come from different nodes and never mix.
- Memory: a descendant with a throwback attachment gives the label from its title and the original text from its target message.
- Tags: `ranges[]` entries whose entity is a `User`. The name is sliced from the text by code points, not UTF-16 units, because tag names next to emoji were truncated otherwise. The URL is the entity's `url` or `profile_url`.
- "Is with" tags: from the context layout title. Only range names inside the clause after " is with " and before the last " at " count. They get kind `with`. Tags merge with dedupe on name and URL.
- Sponsored: a non-empty object under `sponsored_data` or `th_dat_spo`, a non-null `ad_id`, or a typename containing `sponsored`, on the node, its ancestors or its descendants. Detection reads the intercepted data, never the DOM.
- Suggested: a badge text containing `suggested`, or a `tracking` string holding JSON with `originated_from_recommendation` equal to `"1"`.
- Reel: the permalink contains `/reel/` or `/reels/`. Event: it contains `/events/`. Both are flags. Both are parser facts; instance type rules decide whether either is excluded.
- Media: a walk that skips the subtrees `actors`, `feedback`, `badge`, `profile_picture`, `profilePicture`. An image is any `image.uri`, or a `uri` next to a `width` or `height`. Any URI under `/rsrc.php/` is dropped: that is the static host of Facebook's own glyphs (a name's emoji, the privacy icon, a link card's icon), and it is never a post picture. A video URL is the first of `browser_native_hd_url`, `browser_native_sd_url`, `playable_url`. If those fields are absent, accept `videoDeliveryResponseFragment.videoDeliveryResponseResult.progressive_urls` entries with no failure reason and an HTTP(S) `progressive_url`, preferring HD over SD. Select one progressive file per video; DASH/HLS manifests are not direct MP4 files. A video hint is set when a `video` object or one of those keys exists.
- The parser extracts no reaction counts and no comments.

**Friends.** The friends grid truncates at a few hundred people, so the import reads the typeahead payloads too. A friend node has a title and a URL plus a profile picture or a user typename. Its id is the numeric id from the URL first, then a numeric `userID`, `id`, `idString` or `__dr`, then the first path segment of the URL. An opaque grid id never wins. Otherwise the friend would not match the post authors' keys, and the whitelist and the prune would fail. A typeahead hint has `type == "FRIEND"` with `ent_id`, `link_url`, `title` and `img_url`. Both are merged and deduped by id and URL.

Known auxiliary Facebook feed payloads are stored and marked parsed without producing posts: the composer-only viewer response, `GroupsYouShouldJoinFeedUnit`, `PaginatedPeopleYouMayKnowFeedUnit`, and a deferred `page_info` response at `viewer/news_feed`. Profile timelines additionally return `User.profile_tile_sections`, deferred page-info at `user/timeline_list_feed_units`, and known video-player side responses for dubbed tracks, live end screens, cards, share overlays and ad breaks. Recognize these only with their observed path, label and field sets, including video-grid children, `node` timeline roots, and the `profile_pinned_post/pinned_post_story` surface; retain unknown fields and shapes as diagnostics. A timeline response made only of known unavailable-attachment stories without a caption is recognized without creating empty posts or claiming the timeline itself is empty. These auxiliary responses do not establish an empty timeline. Unknown shapes and post/timeline errors remain diagnostics; the narrowly scoped decorative warning exception is defined in 6.17. Listener/parser errors or failed raw ingestion make an otherwise successful collection end as `error`, preserving successfully ingested posts.

## 6.9 Instagram

**The gate** is in the table of 6.4. The path allow list covers feed, user, friendship, media, discover, explore and clips reads. The exclude list keeps writes and noise out.

**Empty profile.** Accept a `user_timeline` or `xdt_api__v1__feed__user_timeline_graphql_connection` with an empty `edges` array and explicit `page_info.has_next_page: false`, without root-level errors. Missing pagination, unrelated empty connections and error responses are not empty-timeline evidence.

**Media candidates.** A node needs a code (`code`, `shortcode`, `media_code` or `pk_string`) and an id (`pk`, `id`, `media_id`, `shortcode` or `code`), plus an author key or a media key. A video node without a code is rejected: stories have no code and stay out.

**A carousel slide is not a post.** Identify entries of `carousel_media` as children and exclude their normalized candidate ids from standalone post candidates. Apply the same parent/child exclusion in both post extraction and candidate-id discovery, so a slide cannot be stored, judged independently, or counted as a fresh post by the scroll loop. A parent-first walk that registers child ids before descending is the reference implementation; a preliminary relationship index or equivalent traversal is also valid. If an id occurs elsewhere in the same payload, its known carousel-child identity still excludes it. Do not publish candidates before those relationships can be resolved. `ExtractPosts` and `ExtractCandidateIds` name these operations, not required public C# symbols.

Profile timeline answers can give slides their own `code`, `pk` and `product_type: carousel_item`, making them resemble standalone media. Home-feed slides can omit both code and product type. Use the `carousel_media` relationship, not a product-type test or missing date/caption, to distinguish them. A genuine standalone post with no date or caption remains eligible. The parent owns all slide media through the carousel field rule below, with slide order and existing deduplication preserved. Reparsing prevents new slide rows; it does not silently delete legacy rows (16.8).

**Fields.**

- Id and like ref: the candidate id (the media pk).
- Time: `taken_at`, `taken_at_timestamp`, `timestamp` or `published`.
- Text: `caption.text`, `caption`, `text` or `caption_text`, else the joined preview comments.
- Author: handle from `user.username` or `username`; pk from `user.pk`, `user.id`, `user_id` or `owner`. The author id is the pk, else the handle. The name is the full name, else the handle. The URL is `https://www.instagram.com/<handle>/`. The pk and the handle register as aliases of one person from the same observation.
- Tags: the coauthors, without a kind.
- Permalink: `permalink` or `share_url`, else `/reel/<code>/` or `/p/<code>/`.
- Reel: product type `clips`, `igtv` or `reel`, or media type 2 or 5, or a reel URL. A reel is a fact, not a built-in exclusion; apply the instance's type and audience rules.
- Images: from `image_versions2.candidates`, else `display_resources`, else `thumbnail_src` or `display_url`. Pick the largest width up to 1080, else the smallest positive width, else the first.
- Video: the first `video_versions[].url`.
- Carousel: all slide images go into one list and all slide videos into a second list, each deduped. A slide's video is therefore numbered after every picture. The page compensates (chapter 15). When the carousel yields no image, the node's own image is used.
- Sponsored: `is_paid_partnership`, `paid_partener` or `is_paid_sponsored` is true; `ad_id_latest` or `ad_id` is a non-empty string or a non-zero number; or product type is `ad`. Values count, never key presence: the keys exist on organic posts too.
- Suggested: `suggested_for_you: true` on the node or an ancestor, or a unit title or feed type in the known set (`discover_media`, `discover_top_media`, `explore_grid_media`, `feed_backtracking_unit`, `feed_follow_requests`, `feed_suggestions_unit`, `media_with_liked_by`, `top_reels_media`, `suggested_for_you`, `suggested_for_you_unit`) or containing "suggested for you" or "for you page".
- Event is never set.

**Empty timeline.** The answer with a `user_timeline` key whose `edges` are empty and whose `page_info.has_next_page` is false. A visit that gets it ends at once as `empty`, not as a failure.

**Following list.** The import opens the owner's profile using `self_username` or the profile redirect, waits for the accessible following-count link, clicks it, requires the following dialog and scrolls inside it (6.11). A missing dialog is a failure, not permission to treat the profile grid as the following list. The gate for this import is the paginated following endpoint alone: the modal's bootstrap payload also carries recommendation and timeline data, and a wider gate would write strangers into the whitelist. A following node has a `username`, no code, and no ancestor with a code, so post authors stay out. The avatar is `profile_pic_url_hd` or `profile_pic_url`.

## 6.10 Ingest and recovery

One application operation, used by collect, process and reparse, holds the platform ingest lock. It processes raw files in name order:

1. Register any complete file missing a snapshot. Stamp AttemptedAt. A failed automatic attempt waits 30 minutes; an explicit reparse can bypass the delay.
2. Parse into observations plus diagnostics. A malformed candidate is reported, never silently treated as proof of an empty feed. Keep the raw. An unrecoverable file failure records Error and leaves Parsed false. Within one file, the first occurrence of a post id wins.
3. Resolve identity and insert/refresh facts. Each insert and content revision change commits deterministic filtering atomically. No media/model call occurs in the transaction. Set readiness null while this content's media inputs are being finalized; keep first-capture provenance and update current-observation provenance (5.12).
4. Reconcile current media slots and fetch missing images promptly. Missing/failed images are reported but do not prevent textual processing forever. Register pending videos without downloading them. Finalize ContentHash, ContentRevision and IngestReadyAt after the image attempts. A crash before readiness leaves this post recoverable from the unparsed snapshot.
5. Mark Parsed true and ParsedAt only after all observations complete; clear Error. Replaying partial work is idempotent. The recovery run reports its own inserted/revised counts and references the original capture run, rather than adding counts repeatedly to a historical run.

The independent process worker recovers due unparsed snapshots and unfinished ingest in bounded file batches, without browser access. It scans complete unregistered raws on a startup recovery pass. Friends snapshots may be replayed only as add-only imports; recovered/offline data never authorizes an unfriend prune. If no recognized posts and no recognized empty answer are found where post data was expected, report parser uncertainty and incomplete coverage; do not claim the author posted nothing.

Rare-voice membership reflects the stored observations for the read, using the exact predicate in chapter 10. Ingest never marks a person objectively quiet. Derived indexes or caches are allowed only with correct invalidation; they do not become a separate authority for that label.

## 6.11 Friends import and the unfriend prune

**Current limitation:** both live adapters report `complete=false` and import add-only. The completeness validator and guarded prune operation exist, but `Capture` does not supply the full live pagination proof. The rules below define the required evidence before that limitation can be removed; terminal-page sightings alone do not authorize pruning.

Live Instagram following-list navigation: open the configured owner profile (or the profile redirect), wait for the accessible `N following` link and click it. Current markup uses `href="#"`; direct navigation to `/following/` can show the profile grid without the list. Require the following dialog and scroll inside it. A friends import with no captured list response fails visibly instead of claiming an empty successful import.

The import writes the whitelist. The prune is the one place a partial capture causes real damage: a truncated list looks exactly like "everyone else unfriended me".

1. Record a friends-kind run with process identity. Refuse when re-login is required; take the browser lock and capture into `friends-<stamp>`.
2. Attach the people-list gate with body dedupe on. Navigate to the list (Facebook `/me/friends`; Instagram the following modal). Scroll with the list cap of 60, well past any real list, with the no-new stop.
3. Register every file as a friends snapshot. Close/sync the browser, export cookies on success and release browser ownership, then take the ingest lock. Parse/dedupe people, download avatars and resolve each person with IsFriend true. Avatar work never holds the browser lock.
4. Determine completeness from the saved authoritative friends/following-list responses. Require a continuous pagination chain from the first page to an explicit terminal marker (such as `has_next_page = false`), or a platform response explicitly documented by the adapter as the complete list. All pages must belong to the same owner and list, and every person must parse successfully. Missing pages, repeated or broken cursors, listener/write/parse errors, a checkpoint, a timeout or the scroll cap make the result incomplete. When an exact total for that same list is available, it must be consistent within the run and equal the deduplicated person count; an approximate display count is not evidence. Typeahead hints may add friends but do not prove the list complete. An adapter without proven completion semantics always skips pruning.
5. Prune only when that evidence proves completeness and at least one person was found. Every current friend of the platform survives when any ref it is known by appears in the fresh capture. Otherwise it loses the flag and the log says "no longer followed". Three idle scrolls stop capture but never authorize pruning. Empty and incomplete captures add what was proven and remove nobody.
6. Finish a clean run as `ok` (no-new stop) or `capped`; preserve `checkpoint` or `error` on failure. `ok` describes the capture outcome, not list completeness. Record `{stop_reason, complete, completion_evidence, expected_count, imported, pruned, prune_skipped_reason}` in `StatsJson`, with raw references for the evidence. Clear the re-login flag only on `ok` or `capped`. Print `friends <platform>: <status> (imported=N, complete=true|false, pruned=M[, prune skipped: <reason>])`.

`friends --offline [--all-dirs]` re-imports from the stored `friends-*` directories, the newest or all. It never prunes, because completeness is unknown. `avatars --platform X` backfills profile pictures from those raws for authors without one.

## 6.12 Timeline visits

Per-profile post counts and date ranges require matching primary-author refs, confirmed Instagram coauthors, or explicit Facebook enclosing timeline-owner evidence (6.17). Background home-feed prefetches do not establish coverage of the visited profile, even when received while its page is open.


A visit in `close_friends`, `all_followed` or a `--person` collect:

1. Navigate to the timeline URL (60 seconds). Check for a checkpoint.
2. Poll every 2 seconds for up to 60 seconds. The timeline selectors present means `rendered`. The empty-timeline flag means `empty`. Otherwise `unrendered`.
3. `rendered`: harvest the embedded payloads, then scroll to the timeline depth. `empty`: log it, done in seconds. `unrendered`: record what the page showed (title, final URL, the first 200 characters of the page text) and save a screenshot `timeline-<handle>.png` in the run directory.
4. Write a TimelineVisits row with author id, render status, capture status, distinct observed count, oldest/newest observed date and stop reason. `captured` requires usable saved post observations; a rendered grid with no usable payload is `incomplete`, not proof of no posts. `empty` requires the explicit empty answer. Count skipped, empty and incomplete separately. For a sweep, commit the terminal visit and target advancement together.
5. Pause 3 to 8 seconds.

One blank page is noise: Instagram serves them under fast navigation. The signal is two unrendered visits in a row on the same profile, within the last 60 days. The status command prints one `WARNING timeline:` line per such profile, with the note and the screenshot path. The debug page shows a banner and the per-profile table. A rendered or empty visit clears the streak. There are no retries inside a run: pacing stays human.

## 6.13 Session health and checkpoints

- A checkpoint is a URL that contains one of the site's URL markers, or a page whose lowercased HTML contains one of the text markers. It is checked after every navigation, before every scroll, and after a failed feed wait.
- On a checkpoint browser activity stops at once, the status is `checkpoint`, the platform's `NeedsRelogin` flag is set, and the log records it. The raws captured so far are still parsed, deterministically filtered and stored. Collection never runs the judge, summaries or pending-video pass. Independent processing continues over safely ingested data despite the checkpoint. The CLI exits with code 1. No browser retries.
- `NeedsRelogin` is cleared by a successful login, or by a collect or friends run that ends `ok` or `capped`.
- While the flag is set: collect writes a `refused` run row; friends records a refused run and like defers its durable request; the scheduler refuses manual requests with "needs re-login" and skips slots. The page shows a banner with the exact login command.
- A logged-in page under a notification modal, or a feed that never renders, is not a checkpoint. Dismiss recognized notification prompts as specified in 6.2; an undismissable recognized prompt or a feed that never renders ends `error` without setting the re-login flag. Other unfamiliar modals require operator inspection; do not treat zero captured posts as evidence that the feed is empty.

## 6.14 Reparse

`reparse --platform X [--run <dir> | --path <dir> | --all | --failed] [--no-network]` uses the same ingest operation and records its own run. Default scope is the newest feed directory; --all includes close-friend and sweep captures but excludes friends-list captures. A foreign directory is registered with imported provenance; original run outcomes are not rewritten.

Reparse never calls the model. Missing or content-stale results become durable pending work for an independent process pass. `--no-network` also disables image fetches for this invocation and registers existing files; it is not an instance-wide pause of automatic processing. For a wholly offline maintenance session, stop/disable scheduling before replay. The old `--no-llm` switch is accepted as a compatibility no-op with a note: reparse already makes no model calls.

Identical replay changes neither post count nor content revision. Changed parser output advances the content revision and deterministically refilters it; successful old model output stays inspectable with its old revision. Policy-only changes still need explicit refilter/rescore. A foreign-directory replay starts running and finishes imported; ordinary replay finishes ok. Print `reparse <platform>: ok|imported (dirs=, found=, new=, revised=, media=, pending=)`; the processing worker has a separate summary.

## 6.15 Adapter validation

This chapter fixes the capture strategy and the current parser contracts. It cannot promise that the payload shapes hold. The platforms change their reads without notice and document nothing.

Bring-up of a platform must produce, before its collector counts as working:

1. The read-path endpoints and their request shapes, expressed as the capture gate.
2. The post id field and the story-type flags: sponsored, suggested, event, reel, shared, memory.
3. One sanitized raw fixture per story type, committed with the tests. The generic JSON/JSONL sanitizer in `tools/sanitize.py` provides a starting point; review its output before committing any fixture.
4. One live pass of the verification checklist (chapter 17).

A shape change later is an offline fix: patch the parser, replay with `reparse`.

## 6.16 Coverage and resumable sweeps

The home feed is a ranked platform surface. Sorting captured posts by date does not turn it into a complete friends history. Timeline depth is also bounded. The application reports observations, never a percentage of all posts captured.

An all-followed sweep snapshots the current whitelist into SweepTargets. Each invocation resumes at the next pending target and visits at most the configured sweep limit; it never restarts from the first alphabetical author merely because a new run began. Mark a target visiting with its run id, then commit its terminal visit and done/skipped/retry state together. A failed/unrendered visit advances past first-pass targets and remains visible as a failure, with bounded delayed retries under 6.17. A checkpoint stops the run and leaves unvisited targets pending. After a dead run, a visiting target without a terminal visit returns to pending. When the cycle is complete, a later run creates a new snapshot; removed friends are skipped and newly added friends enter that next cycle.

Coverage queries show each target's last attempted visit, last successful capture or explicit empty response, observed date range and last failure. For sweeps show attempted/total targets, remaining targets, cycle start and whether this run used up its visit budget. These counts describe visits, not captured-history completeness. `--person` visits contribute coverage without moving a sweep cursor.

Status distinguishes “no new ids observed”, “explicit empty response”, “not checked recently”, “rendered but no matching post payload” or “partial capture with errors” and “blocked/interrupted”. UI staleness uses `ui.coverage_stale_hours`; an author with no visit is never shown as recently checked. A home-feed appearance alone is not a successful timeline check. Coverage gaps do not change category membership or hide posts.

## 6.17 Recovery without an operator

A link-only Facebook share may contain no caption or image. Read its attachment's article title, source and direct web link into the shared-content fields, separately from the friend's own caption. Recognize both titled `ExternalUrl` attachments and attachments with a valid HTTP(S) `story_attachment_link_renderer.attachment.web_link.url`, even when the target marker or title is absent. This fallback requires an absent/`ExternalUrl` target and absent/`GenericAttachmentMedia` media type: native story/memory/photo targets and animated/video media links are not newly classified as external shares. Captions and preview images do not change link-share recognition; an attached original authored story retains precedence. An unavailable attachment may coexist with profile metadata; it remains recognized unavailable content, not an empty timeline.

GraphQL errors retain their message and field path. A strictly recognized `User.profile_tile_sections` response whose errors are confined to that same decorative subtree is a nonfatal stored warning, not failed post capture. Post/timeline errors and unknown shapes remain failures. No generic error suppression is permitted.

Immutable snapshots rejected by the parser store the current parser version and wait for a parser upgrade or explicit replay, instead of retrying identical bytes every 30 minutes forever. Advance the parser version when compatibility fixes can recover older rejected snapshots. Transient ingest failures still retry after 30 minutes.

Within an all-followed cycle, a rendered/incomplete, unrendered or ordinary-error visit advances past first-pass targets but enters delayed recovery: at most three completed attempts per target per cycle, with one hour after the first failure and two hours after the second. Pending first-pass targets take precedence, and retries count against the normal per-run profile budget and use the same pacing/depth. Future-dated retries keep a cycle open. Checkpoints and cancellation never request an automatic browser retry.

After the first pass has attempted every target, the enabled scheduler can dispatch due retries from that existing cycle independently of collection slots. It respects re-login, browser ownership, active collection and manual cooldown; startup failures delay another recovery launch for at least one hour. `collect --mode all_followed --retry-incomplete` selects only due retry targets and never starts a new cycle. Exhaustion remains visible until a future scheduled sweep or explicit profile visit. Close-friend visits continue to recover at their configured collection slots. Historical failures stay historical: replay alone cannot claim a successful new visit.

Coverage distinguishes a partial capture with saved matching posts from a rendered page with no matching post payload. Include errors and their raw/run references, retry time and attempt limit. An external failure may still require login or a future compatibility fix; expose that state rather than promising unattended repair of every site change.

Profile evidence includes Facebook `data.user`/`data.node` timeline responses with an explicit profile id: posts on that wall can have a different primary author. Instagram confirmed `coauthor_producers`/`coauthors` also establish coverage for that collaborator; invited collaborators do not. Keep this evidence separate from stored post authorship and policy eligibility. A home-prefetched post without matching primary/coauthor or explicit profile evidence never establishes target coverage.

Additional narrowly scoped upstream-warning paths are Instagram profile connection `edges/<index>/node/location/profile_pic_url` (a place icon), and Facebook profile `delegate_page/ctx_business_adoption_fact_based_benchmark_page_id` or `delegate_page/ctwa_ad4ad_insights` (business promotion metadata). Require the corresponding observed profile payload shape. Authored text, author identity, actual post media and pagination errors remain failures. Recognized deferred Facebook video-player auxiliaries also occur at `viewer/news_feed/edges`; retain their same label and field checks.

Older Facebook profile-photo posts may render without an article role or h4 heading. Timeline readiness also accepts post/photo permalinks inside the main landmark, while plain Photos/Friends navigation alone does not qualify. This only admits the bounded capture step; successful coverage still requires matched saved post evidence.


### Partial-response recovery (parser version 5)

A fatal GraphQL field error excludes its affected feed edge on recognized Facebook timeline and Instagram timeline connections. Unknown/unscoped errors exclude the whole JSON root. Independent roots in a JSONL response and unaffected sibling edges remain usable; ingest commits those observations even when the raw remains unparsed/blocked with diagnostics. Damaged records never overwrite stored good content. Replay is idempotent and does not rewrite historical run or visit outcomes.

Additional nonfatal warnings are restricted to exact paths and supporting structure: a Facebook top-level comment's `attached_story` under the feedback context of an identified post; a caption's rich-text `ranges/<index>/entity` when complete text and a valid range survive; and Instagram home-feed union auxiliary fields (ad, explore, suggestions and similar wrappers) beside an identified media object with a user. These exceptions do not cover the post's own attachment, missing caption text, author identity, actual media, pagination, unknown fields or missing media in another edge. Errors remain stored as warnings. No missing entity or attachment is invented, and warning-only responses do not prove an empty timeline.

### Explore place-icon alias (parser version 8)

Instagram can report a location-thumbnail failure using the internal alias `_on_Query_xdt_api__v1__feed__timeline__connection_edges_node_on_XDTFeedItem_on_XDTFeedItem_explore_story_media`, followed by `0/node/location/profile_pic_url`. Treat only this exact path as a stored nonfatal warning when the home timeline connection contains exactly one object at `edges/*/node/explore_story/media`, with post id, shortcode, author identity and an explicitly null location icon. The internal zero index is not the outer timeline edge index. Multiple explore-media branches or other indices remain unsupported and diagnostic.

This exception preserves otherwise recognized posts and media without claiming an empty response. Missing identifying fields, absent/non-null icon fields, altered aliases, and errors affecting captions, authors or actual post media remain failures. A second fatal error still blocks its scope even when the icon warning is present. Parser-version advancement makes older rejected snapshots eligible for ordinary recovery; replay does not rewrite historical collection/coverage outcomes.
