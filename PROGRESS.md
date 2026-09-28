# Project status

The application implements the .NET/SQLite/Playwright/static-Razor stack, configurable filters and views, independent model/media processing, resumable collection, persistent diagnostics, and direct/Docker deployment tooling.

Public documentation describes reusable behavior and defaults. It is not an operational status report for any running instance. Use the CLI `status`, `/debug` and private `data/SETUP.md` for live state.

## Current maintenance work

Completed public-repository preparation is recorded in [PUBLICATION.md](PUBLICATION.md): privacy audit, documentation reconciliation, source mapping and the human/AI setup guide.

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
