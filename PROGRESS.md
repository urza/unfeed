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
