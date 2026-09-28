# Code map and design notes

The [numbered specification](docs/README.md) defines behavior and compatibility. This document maps the implemented application and records internal design choices that can change while those contracts hold.

| Responsibility | Source |
|---|---|
| Configuration, taxonomy, preferences and validation | [Configuration.cs](src/Feed.Core/Domain/Configuration.cs), [InstanceFiles.cs](src/Feed.Core/Infrastructure/InstanceFiles.cs) |
| Identity, filter precedence and persisted entities | [Identity.cs](src/Feed.Core/Domain/Identity.cs), [IdentityStore.cs](src/Feed.Core/Application/IdentityStore.cs), [Entities.cs](src/Feed.Core/Domain/Entities.cs) |
| SQLite schema and migrations | [FeedDb.cs](src/Feed.Core/Infrastructure/FeedDb.cs), [migrations](src/Feed.Core/Infrastructure/Migrations) |
| Browser sessions, platform gates and capture | [BrowserSession.cs](src/Feed.Cli/BrowserSession.cs), [Site.cs](src/Feed.Cli/Site.cs), [Capture.cs](src/Feed.Cli/Capture.cs) |
| Payload parsing, ingest and coverage recovery | [PayloadParser.cs](src/Feed.Core/Infrastructure/PayloadParser.cs), [Ingest.cs](src/Feed.Core/Application/Ingest.cs), [TimelineCoverage.cs](src/Feed.Core/Application/TimelineCoverage.cs) |
| Model preparation, bounded dispatch and result application | [Processing.cs](src/Feed.Core/Application/Processing.cs), [Prompts.cs](src/Feed.Core/Application/Prompts.cs), [ModelClient.cs](src/Feed.Core/Infrastructure/ModelClient.cs) |
| Media and retention | [MediaFiles.cs](src/Feed.Core/Infrastructure/MediaFiles.cs), [Maintenance.cs](src/Feed.Core/Application/Maintenance.cs) |
| Durable requests, locks and scheduling | [Actions.cs](src/Feed.Core/Application/Actions.cs), [Ownership.cs](src/Feed.Core/Infrastructure/Ownership.cs), [Scheduler.cs](src/Feed.Web/Scheduler.cs) |
| Feed and diagnostics projections | [Queries](src/Feed.Core/Queries), [Razor components](src/Feed.Web/Components) |
| HTTP routes and background images | [web Program.cs](src/Feed.Web/Program.cs), [Backgrounds.cs](src/Feed.Web/Backgrounds.cs) |
| CLI and reaction sending | [CLI Program.cs](src/Feed.Cli/Program.cs), [LikeSender.cs](src/Feed.Cli/LikeSender.cs) |
| Deployment and verification | [build.sh](build.sh), [Dockerfile](Dockerfile), [compose.yaml](compose.yaml), [tests](tests/Feed.Tests) |

## Internal choices

- One shared core library and two executables. SQLite rows are the durable work ledger; there is no broker or agent service required at runtime.
- Linux ownership uses `flock` on a stable file inode. Releasing a lock clears metadata without unlinking its file. Process identity uses kernel start ticks and boot time to distinguish PID reuse reliably across observers.
- Browser profile staging uses a non-deleting copy with cache/lock exclusions. An instance-started Xvfb is shared across browser workers and stopped explicitly through the noVNC helper. Notification permissions are initialized without overwriting existing choices.
- Processing rotates a persisted `process:turn` cursor across platform/judge/summary pairs. Each turn admits at most `llm.batch`; with two platforms a continuously eligible model task gets an admission opportunity within four turns. Raw recovery rotates independently, while model dispatch and videos share processing ownership.
- FeedQuery uses a deferred SQLite read transaction, in-memory membership evaluation, per-author sorted dates with binary bounds for rare views, and media/feedback lookups. It folds exact repeated images without merging post identities or actions.
- Model results carry content/visibility revisions and exact request provenance. Identity merges invalidate affected content; stale in-flight answers cannot overwrite current decisions.
- ASP.NET serves static Razor HTML and versioned CSS/ES modules. JavaScript enhances layout and actions; core reading, navigation and forms work without it.

## Current limitations

- Platform markup and private response formats can change. Sanitized fixtures protect known shapes; each deployment still needs live login/capture checks. A checkpoint stops browsing and may require the human.
- Live audience imports are deliberately add-only: the completeness validator and guarded prune exist, but the adapters do not yet supply the full proof needed to authorize removals. Facebook imports friends, not followed nonfriend accounts/pages.
- Reaction selectors reject ambiguous controls. Enabling like-back requires deliberate validation of the intended post on each platform; local fixtures cannot establish live correctness.
- Feed reads load stored post projections into memory. The configurable render cap limits rendered cards, not all query work. Large retained histories need measurement before performance claims.
- Some immutable raw failures wait for a parser upgrade; exhausted browser/media retries remain visible. Unattended recovery does not repair arbitrary external changes.
- The feed and noVNC have no authentication. Deployment requires private access. Chromium sandbox support depends on the host; disabling its sandbox is an explicit instance choice.

These limitations describe the current release. They do not weaken the specification's privacy, identity, safe-write or recovery guarantees. Instance-specific evidence and workarounds stay in data/.
