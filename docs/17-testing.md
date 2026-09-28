# 17. Testing and verification

You are here: how you know it works. Every binding rule of chapter 2 and every behavioral contract of chapters 4 to 15 has verification that pins it. This chapter lists what the suite must cover, how it isolates itself, and what only a live pass can verify. Under 2.5, tests protect outcomes, compatibility and failure behavior. They do not require a reference class, queue type, query plan, traversal order or exact private call sequence. Public formats/defaults and versioned hashes still need exact checks. Use benchmark measurements for environment-dependent performance targets.

Source map: [Test project](../tests/Feed.Tests) · [Synthetic fixtures](../tests/Feed.Tests/Fixtures) · [Measurements](../measurements.md).

## 17.1 The suite

- xunit, with the suites runnable through the documented `tests/Feed.Tests` commands. One project referencing the application projects is the reference layout; fixture and supporting-project organization may differ without breaking those commands.
- Each test needing writable instance state gets an isolated temporary instance/database and cleans it up. Copying the sanitized example (`config.json`, `taxonomy.json`, `preferences.md` with fake names) is the reference setup; fixture factories may produce equivalent inputs. No test shares mutable instance state with another. No automated test depends on external services; local HTTP servers for web/browser checks are allowed.
- The example config enables both platforms with slots, disables the model with a dead endpoint, sets jitter 0, the default view to a category, backgrounds off, and port 0. It contains a comment line, so it also tests comment handling.
- Raw fixtures: a sanitized Facebook capture (line-delimited JSON, synthetically constructed) and a sanitized Instagram capture (JSON), produced by the sanitizer script in `tools/`, which keeps enum-shaped strings and rewrites every name, id, code, caption and URL. A new fixture follows the same method. No real name, handle or policy line appears in the tests.
- Model calls use deterministic scripted responses and controllable completion gates. An HTTP handler is the reference fake; an equivalent local endpoint can verify actual request overlap and cancellation. Faking only the high-level worker count is insufficient to prove concurrent HTTP dispatch.
- The fast-suite target is a few seconds on the documented development environment: `dotnet test tests/Feed.Tests -nologo -v q --filter 'Category!=Browser'`. UI browser checks carry the xunit trait Category=Browser and run separately with `--filter 'Category=Browser'`, using Playwright for .NET against a throwaway local host. Neither suite calls real platforms or model services, and neither requires npm or a JavaScript test runner. A variable CI wall clock is not a correctness assertion.

## 17.2 What the tests pin

The cases below define the regression contract for maintenance. The linked test project is the authority for which scenarios have automated coverage; live checks are separate. A missing test or current limitation is a gap to track, not permission to relax the behavior. Current implementation limitations are listed in [the code map](../IMPLEMENTATION.md#current-limitations).

**Small contract edges.** An empty taxonomy generates a prompt requesting categories=[] and still produces a score. A timeline screenshot is served only from its published diagnostic media copy, and raw retention removes the copy; post-media retention leaves it alone.

**Configuration.** An unattended scheduler observes valid file edits on its next tick, including re-enabling scheduling; invalid edits retain the last good complete set and concurrent requests see one consistent snapshot. A missing config yields defaults with every platform paused. The example loads with comments. The home zone resolves an IANA id and falls back. An unknown key is a load error. The taxonomy validates slugs, the single default, the reserved `all`, and every view shape, and rejects a union of unions. The preferences parser reads the five machine sections, hashes the policy, reads and appends learned lines with citations, and throws on a malformed bullet.

**Domain.** Canonical author refs for every URL form and the rejects. Key refs from an id and a URL. The people matcher: accents, every query word must hit, an exact handle first, never a guess. Image dimension/type results for PNG, GIF, JPEG, WebP, malformed and unknown bytes, regardless of the chosen inspector. The summary-need rule and the link domain extraction. The mode aliases. The hidden-owner set is closed.

**Identity and storage.** Two captures without proven author refs retain distinct observed names and null AuthorId, never share author privileges or feedback, and can later resolve through a proven ref. Different URL-only authors do not merge through null platform ids. ClearHidden sets Hidden false, clears the three nullable fields and advances VisibilityRevision in both tracked and bulk paths. Concurrent heart presses create at most one pending Likes row. A two-key observation merges twins and keeps the friend flag. Only a proven-complete capture prunes, and the prune compares refs. The full deterministic result commits with a new post. Reparsing the fixture twice produces one copy of every post. A content change advances the revision and invalidates the old summary for display without losing its provenance. The 64 px glyph guard deletes a tiny download. A video waits for the verdict; a file already in its slot is registered; an offline import defers nothing. Two unrendered visits flag a profile. An abandoned running row is closed after the grace period, and a live recorded process protects it after its browser lock has been released, including standalone commands.

**Visibility and instance policy.** Two synthetic instances with opposite reel policies produce different visibility from the same parsed flags. Test each blocked type, empty blocked-types, restricted/unrestricted audience, none/with/any tag exception, default and explicit bypass sets, and mute precedence. An author view alone never overrides a hide. Refilter applies changed gates and may replace an old model hide with a deterministic exclusion. Reserved thumbs/other hides stay protected. Policy-only stale model hides remain until an explicit passing rescore; content-stale model hides queue processing without prematurely releasing the post. Known author aliases survive handle changes.

**Ordering.** Explicit scoring/summary commands select newest first. Automatic processing selects successive bounded oldest capture-or-attempt batches within each platform/task, moves failures behind other due work, and rotates platforms and model tasks fairly across scheduler restart without draining the rolling pool. The feed remains date descending with null last and obeys the configured render cap, with no hidden 300-row clamp. Category clauses require current-content verdicts; author clauses, including inside a union, admit visible unjudged posts.

**Interrupted ingest.** Under the default restricted audience, a collect that captures an ordinary stranger's post and then checkpoints/errors never exposes it in live or Unsorted mode, regardless of model availability. Under an unrestricted audience the same post may legitimately pass: the invariant is atomic application of the configured rules. Concurrent readers see no row or the committed deterministic result. Inject an evaluator failure: the insert rolls back and the snapshot remains unparsed. Kill after commit: replay does not duplicate it. Default shields still respect type and mute gates; explicit type bypasses behave as configured. Offline/no-model replay never bypasses the chosen deterministic rules.

**Friends completeness.** A 400-person list that stalls after 80 people and three idle scrolls adds those 80 and removes none. Require fixtures for a continuous terminal pagination chain, a missing page, a repeated cursor, a parse failure, a count mismatch, a checkpoint, a cap, typeahead-only hints, an empty list and an adapter with no completion evidence. Only the proven-complete nonempty case may prune. Verify the evidence and skipped-prune reason in run stats and command output; offline imports never prune.

**Parsers, Facebook.**

- The fixture stories yield author, time and permalink.
- The ids match a full extraction.
- Null sponsored markers are noise; dictionary markers count.
- Recommendation tracking marks a unit suggested.
- Untyped story wrappers never become candidates.
- Shared content and "is with" tags stay separate from the caption.
- Reel and event permalinks set their flags.
- Entity ranges slice by code points.
- Static glyph URLs are dropped.
- Run directories classify.
- Typeahead friend hints parse.
- The capture gate keeps the right requests.
- The embedded preload extractor finds the feed payloads and ignores other preloaders.

**Parsers, Instagram.**

- An empty timeline is recognized.
- Feed items, carousels and reels parse.
- A profile `user_timeline` carousel whose children have their own code/pk/media fields yields exactly one parent post and one parent candidate id. Child ids never count as new posts in the scroll ledger; their photos/videos remain attached to the parent exactly once. Repeat with home-feed children lacking code/product_type, misleading or absent carousel_item types, and repeated child appearances elsewhere in the payload. ExtractPosts and ExtractCandidateIds agree. A genuine standalone dateless/captionless post still parses. Reparse is idempotent and never silently deletes preexisting rows.
- Stories without a code stay out.
- Suggested and paid partnership detect by value.
- The following list parses.
- The gate keeps the right responses.

**Profile preferences.** The notification block is written once and keeps the rest of the file; an existing decision, allow or block, is kept; a fresh profile receives a minimal notification-permission block before its first launch.

**Render budget.** The default is 300. With more than 300 matching fixtures, a higher configured cap renders all matches below that cap, with no hidden 300-row clamp; a smaller cap ends the feed at exactly that many posts before folding. Dates sort descending and legitimate null dates last. No paging/infinite-scroll controls appear. Images remain lazy. Configuration rejects nonpositive caps; the 300-card timing target does not become a claim about a 100000-post page.

**Background pool.** Local and cached Bing images coexist. Pin survives restart and changes the named URL on the next load; Unpin/Next resumes rotation and Next picks a different image when available. Remove handles pinned/current/local/Bing images, preserves unrelated files and prevents a removed Bing identity from being redownloaded. Invalid paths, escaping symlinks, pin/metadata names and missing images never expose arbitrary files. Every gallery action is a POST and works without JavaScript; GET feed/gallery/image requests neither mutate files nor call the archive. Fake eight markets with eight-day windows and duplicate identities: download each unique, nonremoved image once at 1920×1080. Pin/removal races with publication, partial downloads, an invalid archive, timeouts, persisted daily/backoff state and a fully offline startup preserve a usable pool or gradient. Blur defaults to 4, rejects negative/nonfinite values, and affects only an overhanging image layer; verify no soft viewport edge or added scrollbar. A local replacement cannot remain hidden behind a stale cached name: test ETag revalidation or the equivalent content-versioned URL behavior. Reference naming, market membership and private metadata formats are not universal expected strings.

**The judge.**

- The first JSON object wins. Bad replies throw. The reason is cut to 20 words.
- The prompt carries the policy, the shared block, the tags, the definitions, the close-friends-only rule, the feedback memory and the learned lines. Assert exact part order: policy block, reply shape, post JSON, images. Two otherwise different posts with identical effective rules have an identical leading policy/reply-shape text prefix, and no image or post-specific text precedes it.
- The version changes with the policy, the taxonomy and the generation. The ordering-only transition defined in 9.3 preserves generation 3 and verdict validity while changing the exact input hash; changed judging instructions or post JSON shape advance the generation.
- Against the fake endpoint: parallel calls overlap and every verdict lands. Use completion gates, not elapsed-time sleeps: with parallel 4 and batch 4, hold one first-batch request open, complete the other three, and require three next-batch requests to arrive before releasing the held request. Repeat across a platform/task boundary, with batch smaller than parallel, and for summaries. Repeated tails or waiting for a complete batch fail the test.
- A temporarily blocked database apply does not prevent sending inputs already prepared and authorized for dispatch within the buffering bound. Count actual outstanding HTTP requests, not just active Tasks; the shared peak never exceeds llm.parallel, including fallback and mixed judge/summary work. No context is used concurrently and no transaction spans HTTP/image processing; separate contexts or batched writes are valid implementations. Reserve selected ids before dispatch: a still-unapplied, failed or superseded item cannot be selected again in the same invocation, including explicit commands that bypass retry delay. Exact private worker/queue topology is not asserted.
- Unprocessed rows only by default. Hides below the threshold. Fails open on a bad reply.
- Single-post and author scopes select the right rows.
- An always-show author is never hidden.
- A wholly failed batch closes admission/dispatch for its task/configuration and sets the shared backoff. Gate queued and already-authorized work separately: queued unsent items remain pending without new attempt timestamps; authorized primary/fallback attempts settle and apply. New rows and the other platform cannot bypass the stop; raw recovery and unaffected tasks remain eligible. Superseded valid responses and all-short inline summary batches do not falsely declare an endpoint outage.
- The summary prompt carries the language rule.
- A failed verdict stays pending across a restart. Advance a fake clock by 30 minutes and dispatch processing without any new collect: it retries the post. Repeat for summaries and while browser re-login is required. Model-disabled passes still ingest and handle eligible media; paused platforms receive no automatic processing. No same-pass or early retry occurs.
- An automatic backlog larger than llm.batch continues in the same child and pool without a scheduler delay or process restart. Platforms and judgment/summary tasks receive fair selection turns. A separately supplied process --limit N admits at most N rows per stage across platforms; failures, superseded results and inline summaries consume this budget. Repeated failures move behind other pending work. Policy-only stale successful verdicts do not enter the automatic queue; changed content does. Multiple platforms and explicit rescore commands cannot multiply llm.parallel because they share processing ownership.
- An all-short summary batch followed by a long post completes inline, continues selection, and sends the long post. It stops at an explicit limit if the short rows exhaust that limit; an empty hand-over is not mistaken for exhausted scope.
- Cancellation and exceptions in selection, image preparation, work and apply have their specified outcomes. Unexpected faults cancel peers; cleanup ends any queues and observes all started work. No request survives command exit and no cancellation loop spins. With a backlog larger than the current prepared work, a normal operator cancellation stops without draining it, releases the lock, and records cancellation rather than endpoint failure. Disable scheduling, cancel, change configuration and start another invocation: it acquires ownership and uses the new snapshot without an instance failure backoff. Actual stamped attempts keep their retry delay.

**Like-back.** The selected control belongs to the requested post, never a comment or counter. Cover the observed accessible names, already-done states and 16/24 px regression shapes even if the implementation uses a better scoped selector. A selector returning an ambiguous target fails without a click. The queue is per platform with an orphan pass.

**Request recovery and ownership.** Use fake processes/clocks and a throwaway SQLite instance. Recreate the scheduler with no in-memory tracking. Pin crash before spawn, stale-token child, live child surviving parent, pid reuse, durable completion before observation, dead worker without outcome, duplicate submissions, collect TTL and disabled scheduling. Release a collect's browser lock while keeping its ingest process alive: reaping must not close it and a like/login must be able to use the profile. Processing during a checkpoint must not launch a browser. Explicit model commands and automatic processing share one lock; ingest/model lock ordering cannot deadlock with browsing.

Automatic processing requests have Platform null and coalesce work from all enabled platforms. Concurrent queue attempts produce only one active instance-wide request through the partial unique index, not SQLite's nullable platform uniqueness. Restart with a live multi-platform child does not spawn another. Under sustained backlog, verify the chosen platform/task service bound and repeat across restarts; do not require exact reference cursor keys for an alternative fair algorithm. An unexpected worker/startup failure applies the instance-level launch delay, while a task failure leaves unrelated recovery/tasks eligible.

**Deferred hearts.** Queue a heart while re-login is required, wait beyond the collect-request TTL, then clear the flag through successful login. The next enabled tick sends it without another press. Repeat with a checkpoint before mutation and with a heart queued after the sender's last sweep. Kill after `AttemptedAt` commits: recovery fails that heart as outcome unknown and never clicks it again; unattempted hearts resume. Already-liked Instagram posts settle sent without a click. A missing CLI or failed startup produces a visible failure without repeated spawning. Disabling like-back fails pending hearts without opening a browser.


**Application boundaries.** Routes and commands use the same queue, rule and processing operations. Domain tests need no EF Core, browser or HTTP service. Read projections do not mutate or launch work. No private category key or author is required for behavior: run the same contracts against independently named synthetic taxonomies and policies.

**Content provenance.** Identical replay preserves content revision. Changed caption, shared content, tags or image identity invalidates current membership/summary and queues work. URL signing changes alone do not. Snapshot a model call, change the post or unhide/refilter it, then return the old answer: conditional apply must discard it. Record the actual fallback endpoint, input digest, model and successful time. Retention preserves known byte hashes and does not cause spurious content revisions. Short-summary markers are revision-specific.

**Vision preparation.** Verify the documented limits, image selection, MIME handling, resized geometry and exact sent-byte digest. An alternate processor records its effective preprocessing policy/version. If a prepared-image cache is used, changing source bytes or preprocessing settings invalidates reuse, eviction preserves originals, and cache files are not served as public media. Do not require a subprocess per image or a particular encoder's byte-for-byte output when those bytes are correctly versioned and recorded.

**Ingest recovery.** Kill between raw rename and registration, between post commit and image completion, and before snapshot Parsed is committed. A startup processing pass recovers without another browser run, under ingest ownership. Partial image success does not suppress missing-slot recovery. Parse failures retain raws and visible diagnostics; automatic retries obey the delay. Retention skips unparsed/in-use captures. Changed source media gets an immutable replacement path and retired media is not rendered as current.

**Coverage and sweeps.** With more targets than the sweep limit, successive runs cover later targets instead of restarting alphabetically. Restart mid-target, checkpoint mid-cycle, remove a friend, add a friend, merge an identity and finish a cycle. Advancement and terminal visits are atomic. Failed visits advance the cycle but remain failures; unvisited profiles remain pending. A rendered grid with no payload is incomplete, an explicit empty response is empty, and a home appearance is not a timeline check. Backfilled observations may change a rare-voice count. No coverage percentage claims total platform-post completeness.

**Setup and validation.** Rules reporting is read-only and excludes secrets. Validate closed filter values and bounds. Show unmatched/ambiguous people and effective bypasses. An agent-authored instance and a manually authored equivalent have identical behavior. No particular policy sentence, personal category name, private endpoint or handle exists in fixtures or code.

**Retention.**

- The hidden grace counts from the hide.
- A hidden video goes after a day.
- Old visible videos go.
- Orphan folders go.
- Raw retention removes the run directory.
- Hide and unhide move the clock.
- The knobs load with their defaults.

**Views and stacks.** The rare view admits quiet friends and waits for the judge. Compare production membership/count/badge results with a simple fixture oracle at inclusive boundaries and equal timestamps, with hidden posts, backfills, date corrections, identity merges and friend/parameter changes. If caching is used, test invalidation; SQL versus in-memory computation is not pinned. The union view deduplicates, counts correctly and names the first admitting member. Category-and-authors views require both a matched author and a current-content category verdict, with the model on or off. Cover wrong categories, other or unresolved authors, empty lists, hidden/unjudged/content-stale posts, union deduplication and first-member explanations; authors-only views still admit unjudged posts. The rules report must show both constraints. Reject other mixed shapes and unknown category keys. Malformed views are rejected. The card trail lists the layers. A burst folds behind its newest post.

**Web.**

- The helpers: linkify, tags, relative time, the empty-state messages, the scheduler's deterministic jitter.
- The host boots on a random port against a throwaway instance.
- An empty instance renders an empty feed and answers the health route.
- A seeded instance renders cards and the hidden view.
- The action routes write only through POST.
- The media route refuses a path outside the media folder.

**Frontend.** Razor renders useful HTML before JavaScript runs. Local Playwright for .NET checks, with JavaScript disabled, verify navigation, dropdowns, text/stack expansion, access to all media and POST/redirect actions. With JavaScript enabled, verify masonry and scroll anchoring, gallery keyboard/focus behavior, thumbs updates and heart polling against local fakes. Check stable visible geometry/behavior rather than a private DOM hierarchy or debounce implementation. Published CSS and every transitive JavaScript import resolve to compatible deployed content; an older cached entry cannot combine with incompatible new dependencies. Build/publish succeeds without separately installed Node.js or npm.

**Operations and measurements.** Concurrent CLI log append/rotation preserves parseable lines without changing operation outcomes when logging fails. Required log formats and operator entry points remain stable even with a different sink or source layout. Benchmark representative render/query and model workloads under documented conditions; compare memory, latency and throughput before accepting a tuning change. Functional tests do not pin nonunique index names, SQLite cache sizes or an unqualified wall-clock threshold.

## 17.3 What only a live pass can verify

The browser sessions, the login, a real collect and scroll, the like sender, profile staging, the virtual display, the video and image tools, the real model endpoint, the background fetch, and the scheduler spawning children. Before a platform counts as live, run the checklist:

1. `login` succeeds and the flag clears.
2. Each enabled collection mode produces an explained bounded outcome (`ok`/`capped`, usable captures or explicit empty responses). Verify embedded Facebook batches when available; do not confuse zero posts or a rendered page with proven empty coverage.
3. Only if the owner opts into like-back: with that platform's flag on, one deliberately selected real heart with `like --post-id N` lands on the intended post and the chip fills.
4. An independent process pass judges an image post against the real endpoint and stores categories plus input/content provenance. It works without a new collect and while browser work is blocked for re-login.
5. A friends import reports its completion evidence and the observed count; an unproven list prunes none.
6. A bounded sweep resumes on a later run and its coverage display matches the visited profiles. Restart the host with live/unfinished processing and verify recovery without a duplicate browser session.
7. Against local vLLM, process several model batches with staggered request durations and record client/server request counts and batch ids. Check that preparation/accounting boundaries do not repeatedly drain available calls while eligible inputs remain, and inspect prefix reuse with the constant-first prompt. Record workload, concurrency, completed posts per minute and token throughput; neither active Tasks nor GPU memory usage alone proves saturation.
8. Open the combined background gallery, pin an image, reload the feed and restart the host. The pin persists, the named image changes without the old ten-minute cache delay, and the blur edge remains outside the viewport. With Bing offline, feed/image requests still use the local pool or gradient. Record large-render-cap page size/time separately from the default 300-card benchmark.

Validation of all-followed mode includes a sweep beyond a pilot. Validation of opted-in Instagram like-back includes a photo post exposing the comment/post-control distinction. Read-only deployment does not require sending any heart. Record actual verified/unverified build status with dated measurements or operator notes; this specification does not claim those checks have run.

Notification regression checks cover first-launch permission blocking, preservation of existing decisions, dismissal scoped to the recognized dialog, supported localized buttons, and refusal of ambiguous prompts without touching authentication dialogs. Process recovery must retain a live worker across independent OS observers and distinguish a mismatched process start from the original worker.

Recovery hardening regressions cover link-only shares, unavailable attachments with extra profile metadata, scoped decorative GraphQL warnings versus real timeline errors, parser-version blocking and explicit replay, persisted model failures with revision guards and successful clearing, rejection of truncated final replies, exact-image-repeat folding while preserving identities/actions and separating different/missing content, and bounded delayed timeline retries that cannot begin a new cycle. Verify paused scheduling and re-login gates, recovery launch backoff, accurate partial-capture labels and ordinary view/stack behavior.
