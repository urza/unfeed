# Public repository preparation

The public tree describes the reusable application. Instance policies, accounts, endpoints, credentials and operational notes belong in ignored `data/`.

## Completed

- Reviewed tracked and unignored files, synthetic fixtures, deployment exclusions and reachable Git history. Compared public text with private account identifiers, post identifiers, endpoint/credential values and policy sentences; inspected additional credential/address patterns. No private account or credential matches were found. Historical generic machine paths and illustrative addresses do not identify an instance; the public baseline is consolidated into a single initial commit, with earlier local history backed up privately.
- Archived earlier development/instance notes and obsolete pre-implementation reviews under ignored `data/`. Public progress and design notes now contain reusable engineering information. Setup notes belong in `data/SETUP.md`; none are shipped as a root setup file.
- Kept the 17 numbered chapters as behavior contracts, removed obsolete implementer framing, added source maps and labeled current limitations. Reconciled Docker deployment, profile staging, unique reaction targets, durable recovery fields/cursors and quiet diagnostics with the implementation.
- Rewrote the README around the product vision and human/AI setup path. Updated AGENTS.md and the runbook for configuring and operating the existing application. Personal policy stays in the three instance files.
- Excluded environment-secret files from both Git and Docker contexts. The default data directory remains excluded; a custom in-repository instance path needs explicit exclusions too.

## Verification

- Clean export of public working-tree files contains no data directory. Locked restore and Release publish succeed there; 123 fast tests pass.
- Fresh-instance init/rules and published health/feed/debug/background-gallery checks pass. Init does not manufacture the three personal configuration files. The disposable server is stopped after verification.
- Markdown/generated-HTML local links, generated table structure, Compose configuration and whitespace checks pass. Generated spec.html matches the numbered chapters.
- This documentation/privacy work changes no application runtime behavior and does not restart or reconfigure an existing instance. It does not substitute for deployment-specific platform/model/browser checks.

## Before publication

The project uses the author's custom [license](LICENSE), reproduced verbatim. The reviewed application and documentation form one initial repository snapshot. Publish only Git-tracked source, never a recursive archive of a live checkout containing data/.

For future releases, repeat the privacy scan on changes and history, check staged paths, regenerate documentation, and run the relevant verification. Keep scan details involving real identifiers in the private instance, not in this public record.
