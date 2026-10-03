# Project status

The application implements the .NET/SQLite/Playwright/static-Razor stack, configurable filters and views, independent model/media processing, resumable collection, persistent diagnostics, and direct/Docker deployment tooling.

Public documentation describes reusable behavior and defaults. It is not an operational status report for any running instance. Use the CLI `status`, `/debug` and private `data/SETUP.md` for live state.

## Current maintenance work

Completed public-repository preparation is recorded in [PUBLICATION.md](PUBLICATION.md): privacy audit, documentation reconciliation, source mapping and the human/AI setup guide.

Repost attribution: summary requests now preserve caption/shared speaker boundaries, and sharing attribution remains visible above collapsed text. All 157 non-browser tests pass, including original/unknown/shared speaker requests and request-hash provenance; the browser regression passes with and without JavaScript. Release CLI/Web builds pass; summary/UI contracts and combined specification updated. Instance category refinements and bounded live checks remain private.

## Verification baseline

The latest combined application verification passes all 150 tests: 137 fast tests and 13 local browser tests. Tests use synthetic data and disposable instances. Platform logins, model availability, host Chromium support and opt-in reaction targets need deployment-specific validation under [chapter 17](docs/17-testing.md).

See [the code map](IMPLEMENTATION.md) for current limitations and [measurements](measurements.md) for the scope of synthetic performance results. No personal policies or live service state belong in this file.

## Scheduler configuration extensions

Support calendar-day intervals for scheduled collection and selected profile visits alongside home collection. Preserve existing slot windows, jitter, browser ownership, filtering and recovery behavior.

- Inspect collection, scheduling, configuration validation and operational contracts: complete.
- Implement optional interval and home-profile settings, including Debug and rules visibility: complete.
- Verify date boundaries, restart stability, validation and profile selection with synthetic tests: complete. All 136 fast tests and seven local browser tests pass.
- Update affected specification chapters and regenerate the combined specification: complete.
- Locked Release publish: complete. Deployment validation and operational evidence remain in ignored instance storage.
- Browser verification exposed a literal Razor conditional in the Debug link; corrected both desktop and mobile rendering.

## Facebook reaction targeting

Implemented permalink-matched Facebook post-dialog targeting, excluding background and comment controls and preserving failure on ambiguity. Added three synthetic browser regressions; those and the existing Instagram control test pass. Fixed the explicit like command’s boxed integer/SQLite long-key mismatch, with a passing CLI integration regression. All 137 fast tests pass across the full suite and targeted CLI rerun. Updated the reaction contract and rebuilt the combined specification. Live DOM captures, selected targets and deployment evidence remain in ignored instance storage.

## Instagram reel reaction targeting

Implemented Instagram permalink validation with singular/plural reel URL normalization. Post hearts must fit inside the viewport and pass a center hit test, excluding prefetched offscreen reels and covered controls. Ambiguity includes actionable and already-liked states. Three new synthetic browser regressions cover these cases; all seven focused Facebook/Instagram reaction tests pass. Release CLI publish and specification regeneration pass. Instance-specific deployment and selected-post verification remain in ignored storage.

## Toolbar handoff

Apply the supplied toolbar design to the existing server-rendered controls: inline platform icons, glass/pill spacing, and a single-row picker layout below 698 px of content width. Preserve inspection access, collection gates and plain POST/link behavior.

- Inspect handoff, current toolbar, responsive layout and browser tests: complete.
- Implement styling, icons, responsive controls and accessible names: complete. Preserve the existing centered chevrons and mobile inspection links; keep phone panels inside the viewport.
- Verify resize boundaries, narrow menus, platform state, and operation with/without JavaScript using synthetic browser data: complete. All four targeted browser cases pass (toolbar with/without script, existing web actions, existing layout/gallery). Screenshots visually reviewed at desktop and phone sizes.
- Update the UI chapter and regenerate the combined specification: complete. Release Web publish passed; deployment evidence stays in private instance notes.

## Background gallery maintenance

Expose the gallery directly in the feed toolbar. Normalize Bing regional image identities and consolidate identical cached downloads while preserving pins and remembering removals. Add synthetic regression coverage, update the contracts and regenerate the specification. Five focused background tests and both toolbar browser cases pass, including desktop/phone layouts with and without JavaScript. Synthetic screenshots reviewed; specification regenerated. Existing duplicate cleanup runs during background maintenance after the updated host starts. Exact-content matching does not identify differently encoded legacy copies.

## Custom background uploads

Add gallery instructions and a native multipart multi-file upload form. Validate format and bounded sizes, assign collision-free local filenames, publish complete files and refresh the gallery immediately. Seven focused tests pass, including a script-disabled browser upload of multiple synthetic files, asset retrieval, pinning, invalid format rejection, size/count limits, and a narrow-screen layout check. Reviewed the synthetic phone screenshot. Contracts and combined specification updated; Release Web publish passed.

## Judged posts without categories

Allow empty category arrays in model replies, separate category eligibility from visibility, and preserve explicit optional default-category behavior. Verify parser validation, restriction behavior and All/category/Unsorted/rare membership with synthetic data; update contracts and regenerate specification. Complete: 143 non-browser tests pass, including empty-array parsing, current-version selected rescore/unhiding, and All/category/Unsorted/rare membership. Added `rescore --post-ids` for bounded reviewed repairs with the existing worker and locks. Release CLI/Web builds and specification regeneration pass. Live validation and repair evidence remain in ignored instance notes.

## Platform story context for classification

Preserve the platform-generated story title separately from the author caption through parsing, storage, content revision, model input and card rendering. Pin replay invalidation and caption separation using synthetic fixtures; migrate the nullable field and update contracts. Implemented and validated with 146 non-browser tests, including story-title isolation and replay invalidation. The replay regression also pins kind-independent UTC timestamp hashing across SQLite round-trips. Release CLI/Web builds and specification regeneration pass; live evidence is kept privately.

## Bounded token retries and partial capture recovery

Persist token-exhaustion state and use a configurable delayed budget ladder shared by processing, scheduling and diagnostics. Preserve request provenance and stop automatic escalation at the configured cap. Isolate capture errors to affected records; retain narrowly identified auxiliary errors as warnings while keeping incomplete content diagnostic. Complete: 154 non-browser tests pass, including delayed/capped retries across worker instances, request provenance, content-reset behavior, and partial-response isolation/idempotent ingest. Release CLI/Web builds pass; contracts and combined specification updated. Bounded live replay evidence remains in ignored instance notes.


## Management page

Implement platform status and queued actions, searchable people with close-friend/mute/always-show/removal controls, and editable schedules. Surface rules, categories/views, processing and retention settings with diagnostics retained. Preserve file-based configuration through validated, atomic, conflict-detecting edits; immediately reapply deterministic rules after relevant edits. Verify synthetic settings conflicts, identity resolution, worker gates and browser forms before deployment.

- Inspect current configuration, filtering, scheduler and UI contracts: complete.
- Implement management services, forms and worker dispatch: complete.
- Verify behavior, document contracts and deploy: complete. All 166 non-browser tests and four focused browser cases pass. Migration preserves existing claims; forms work with and without JavaScript, reject stale edits and protect management POSTs with antiforgery tokens. Release builds and combined specification regeneration pass. Synthetic desktop/phone screenshots reviewed, including per-person phone cards. Form-security keys remain private to the instance. Operational evidence remains private.

## Management navigation cleanup

Consolidate feed entry points into a styled Manage button on desktop and phones. Share management navigation with a Backgrounds tab, move gallery forms to `/manage/backgrounds`, and preserve legacy gallery redirects. Complete: five focused browser cases pass, covering responsive controls, gallery uploads/pinning, legacy redirects and native management navigation. Debug/Release builds pass; updated specification regenerated. Read-only desktop/phone deployment checks pass; instance evidence remains private.

## Shared-link context repair

Preserve direct web-link attachment metadata when the optional external-target marker is absent. Add synthetic parser-to-prompt regression coverage, verify offline replay and targeted judgment, then assess video enrichment options. Complete: 169 non-browser tests pass; Release build and regenerated specification pass. Bounded live replay restores missing link context and targeted judgment applies the existing exclusion. Video enrichment assessment favors metadata and available captions first, then bounded frame sampling; no enrichment behavior changed. Private operational evidence stays in the instance.

## Video context and shared-link regression audit

Add optional bounded metadata/caption enrichment before judgment, with private source-keyed caching, exact input provenance and fail-open retrieval. Verify source selection, caption attribution/truncation, cache/failure/cancellation behavior, and pipeline integration. Audit the shared-link parser against synthetic boundary cases and saved captures without bulk-changing historical visibility. Document current video limits and future frame/transcription/video-model options. Complete: 204 non-browser tests and Release CLI/Web builds pass; specification regenerated. Parser fallback now excludes native memory/GIF targets, and saved-capture comparison found no changes outside shared context. Isolated model checks cover permitted linked updates and an expected exclusion. Bounded live metadata/caption extraction and production judgment checks pass; failures preserve prior valid results. Current video limitations and optional future upgrades are documented. Private evidence and deployment state remain in the instance.

## Person feed pages

Add internal author navigation, stored person/rule details, category filtering and person-scoped hidden/unsorted inspection. Carry an exact bounded profile target through the durable collection queue while preserving pause, login, cooldown and browser ownership gates. Verify query isolation, request recovery/dispatch and native browser navigation; update contracts and regenerate the combined specification. Complete: 207 non-browser tests and five focused browser cases pass, including person navigation and protected forms with and without JavaScript. Person browser tests wait for saved feedback and settled single-column resizing; synthetic phone screenshots reviewed. Debug and Release builds pass, and the combined specification is regenerated. No live platform collection was run for this change.

## Person collection through stored history

Keep explicit person visits moving through already-stored posts until their scroll cap. Stop early only after three steps without scroll movement, document growth or additional distinct visit posts; preserve login and explicit-empty stops and ordinary collection freshness behavior. Complete: progression/stall browser check and three focused decision tests pass. The non-browser run passed 209 cases; its migration case passed an isolated rerun after a transient SQLite-pool disposal error. Release CLI/Web builds, documentation/spec regeneration and deployment verification pass. Bounded live validation confirms person collection reaches its configured depth and ingests additional older posts; account-specific evidence remains private.

## Operator recovery clarity

Implement action-oriented processing health, grouped retries and separate informational notices, local-time retry guidance, and collection context using saved capture associations. Preserve technical diagnostics and retry behavior. Refresh only read-only summaries so forms and expanded details remain stable. Complete: structured recovery dispositions, grouped guidance, separate notices, selected capture-run reports and recent successful classifications are implemented. All 213 non-browser cases passed; the three focused recovery cases and both management browser cases passed together. Browser-host startup timed out when mixed with the full suite; standalone browser verification passed. Desktop/phone bright-background checks, Debug/Release builds and specification regeneration passed. Retry behavior is unchanged. Recent completions include first attempts and retries because durable per-attempt success history is not stored. Synthetic verification does not establish live platform compatibility.

## Instagram explore location-icon warning

Recognize the exact internal GraphQL location-thumbnail error alias only with the observed single identified explore-media branch and null place icon. Retain unknown shapes and content errors as failures. Complete: parser version 8 recognizes this narrow shape; 16 new positive/negative regression cases and all 229 non-browser tests pass. Saved-response read-only validation confirms the intended warning conversion while unrelated malformed captures retain diagnostics. Updated the collector contract and regenerated the specification. Deployment and replay evidence remain private.

## Remaining partial-capture recovery

Recognize expanded Facebook composer/privacy responses and intact Instagram Explore branches. Treat narrowly identified missing metadata as partial evidence with explicit warnings, never as proof of full link/content completeness. Preserve existing posts on partial ingest and newer source observations on historical replay. Verify positive/negative parser cases, ingest ordering and interruption recovery, then deploy and repair saved captures without overwriting newer records. Complete: 292 non-browser tests and Release CLI/Web builds pass. Saved-response parsing and isolated offline replay validate the reviewed compatibility cases without post revisions or new media. Collector/UI contracts and operator guidance are updated; the combined specification is regenerated. Private repair evidence and deployment state remain in the instance.
