# Project status

The application implements the .NET/SQLite/Playwright/static-Razor stack, configurable filters and views, independent model/media processing, resumable collection, persistent diagnostics, and direct/Docker deployment tooling.

Public documentation describes reusable behavior and defaults. It is not an operational status report for any running instance. Use the CLI `status`, `/debug` and private `data/SETUP.md` for live state.

## Current maintenance work

Completed public-repository preparation is recorded in [PUBLICATION.md](PUBLICATION.md): privacy audit, documentation reconciliation, source mapping and the human/AI setup guide.

## Verification baseline

The latest application verification includes 123 fast tests and seven local browser tests. Tests use synthetic data and disposable instances. Platform logins, model availability, host Chromium support and opt-in reaction targets need deployment-specific validation under [chapter 17](docs/17-testing.md).

See [the code map](IMPLEMENTATION.md) for current limitations and [measurements](measurements.md) for the scope of synthetic performance results. No personal policies or live service state belong in this file.
