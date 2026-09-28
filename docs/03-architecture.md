# 3. Architecture

You are here: the map of the whole system. The overview (chapter 2) fixed the scope. This chapter names every part, shows how data flows between them, and tells you which chapter details each part. Read it once in full. Come back to it whenever you lose your place.

Source map: [Core](../src/Feed.Core) · [CLI](../src/Feed.Cli) · [Web](../src/Feed.Web).

## 3.1 The system in one picture

```
                         the owner's machine
 ┌──────────────────────────────────────────────────────────────────────┐
 │                                                                      │
 │   instance directory  data/                                          │
 │   ┌──────────────────────────────────────────────────────────────┐   │
 │   │ config.json  taxonomy.json  preferences.md   (owner's rules)  │   │
 │   │ feed.db (SQLite)   raw/   media/   profiles/   logs/   locks/ │   │
 │   └──────────────────────────────────────────────────────────────┘   │
 │          ▲ read/write            ▲ read/write            ▲ read      │
 │          │                       │                       │           │
 │   ┌──────┴───────┐        ┌──────┴───────┐        ┌──────┴───────┐   │
 │   │  Feed.Cli    │        │  Feed.Web    │        │   browser    │   │
 │   │  one command │◄───────│  web host +  │        │  (the owner) │   │
 │   │  per process │ spawns │  scheduler   │◄───────│  reads the   │   │
 │   └──────┬───────┘        └──────────────┘  HTTP  │  page        │   │
 │          │ Playwright                              └──────────────┘   │
 │   ┌──────┴───────┐                                                   │
 │   │  Chromium    │  logged in as the owner                           │
 │   └──────┬───────┘                                                   │
 └──────────┼───────────────────────────────────────────────────────────┘
            │ HTTPS, the owner's own IP
     ┌──────┴───────┐      ┌──────────────┐
     │  Facebook    │      │  LLM endpoint│  OpenAI-compatible, local first
     │  Instagram   │      │  (vLLM etc.) │  called by Feed.Cli processing workers
     └──────────────┘      └──────────────┘
```

Three parts run on the owner's machine:

- **The instance directory.** Everything personal, in one folder. The owner's rules are three text files. The database is one SQLite file. Chapter 4 describes the files, chapter 5 the database.
- **Feed.Cli.** A console program. One command per process. It does every browser action and every batch job: collect, parse, filter, judge, download media, send likes, prune. It exits when done. Chapters 6 to 9, 11 and 13.
- **Feed.Web.** A web host that serves the page and runs the scheduler. The scheduler starts Feed.Cli as a child process for every run. The page never starts a browser. Chapters 12, 14 and 15.

Two external systems are used:

- **The platforms.** Facebook and Instagram, reached only through Chromium under Playwright, logged in as the owner. Chapter 6.
- **The language model.** Any OpenAI-compatible chat endpoint. A local server is the first choice. Chapter 9.

## 3.2 The tech stack

The backend and frontend stack are fixed below, except helper choices explicitly marked as reference implementations. Chapter 14 defines rendering and chapter 15 defines UI behavior. These technology choices do not prescribe an internal class hierarchy or require custom implementations of facilities available in maintained libraries.

| Part | Choice |
|---|---|
| Language and runtime | C# on .NET 10 |
| Web host | ASP.NET Core minimal API |
| Page rendering | Razor Components with static server rendering, compiled by .NET |
| Browser interactions | Plain JavaScript, native ES modules and browser APIs |
| Styling | Plain CSS with custom properties; no preprocessor or CSS framework |
| Frontend tooling | .NET build/publish and static assets; no Node.js, npm, TypeScript, bundler or transpiler |
| Database | SQLite through EF Core, WAL mode, migrations |
| Browser automation | Playwright for .NET, Chromium, persistent profiles, response interception |
| Language model client | OpenAI-compatible chat completions over `HttpClient`, in ModelClient |
| Scheduler | Hosted background service, 5-second tick, durable state in SQLite |
| Video download | `yt-dlp` as an external process, fallback only |
| Image shrink for the model | `ffmpeg` shrinks vision inputs; the size/provenance contract is in 7.6 |
| Logging | `Microsoft.Extensions.Logging` with rotating file and console sinks |
| Tests | xunit |

Dependency policy: prefer the standard library for simple work. Use a maintained library when it materially reduces infrastructure code and maintenance. Avoid duplicate packages for the same job. Versions are pinned by central package management and checked-in lock files. Chapter 16 defines the baseline and selection criteria. No message broker or distributed scheduler is needed.

## 3.3 Solution layout

**Current organization.** The tree maps the source responsibilities. The build/CLI entry points, `out/`, instance paths and test commands remain the documented operator interfaces. Internal folders, class names, number of helper assemblies and placement of an interface may change while preserving the dependency and execution boundaries below. Extra layers are not required merely to reproduce this tree.

```
Feed.slnx
src/Feed.Core/      shared library, no executable
  Domain/           identity, filter rules, categories, normalization, platform registry (pure, no IO)
  Application/      operations and interfaces: ingest, process, queue likes, recover requests, retain
  Queries/          feed, coverage, status and debug projections; no mutations
  Infrastructure/   config, EF Core, stores, raw files, media, HTTP model client, locks, logging
src/Feed.Web/       thin routes, rendering, scheduler and composition root
  Components/       Razor pages, cards, toolbar, stacks and debug sections
  wwwroot/css/      plain CSS, shared design tokens and responsive layout
  wwwroot/js/       native JavaScript modules for layout, gallery and actions
src/Feed.Cli/       thin commands, composition root, Playwright adapters and browser sessions
tests/Feed.Tests/   one test project, with a sanitized example instance and raw fixtures
tools/              noVNC display helper and capture sanitizer
docs/tools/         documentation HTML builder
docs/               this specification
data/               the instance directory, ignored by git
build.sh            publishes both executables into out/
```

The dependency direction is one way: the web and CLI entry points depend on shared application/domain logic, which does not depend on either executable. Domain rules use no IO, EF Core, Playwright or HTTP types. Application operations coordinate those rules and infrastructure; read projections do not invoke mutating operations. The current tree places these responsibilities in Feed.Core and wires them at the two executable roots. Interfaces isolate external effects where useful; one interface per class and additional assemblies are not required.

CLI commands and HTTP routes validate transport inputs and call the same application operations. They do not duplicate queue rules, filtering, selection, or recovery. Browser workflows use a platform adapter implemented in the CLI; the web host cannot resolve or launch a browser from a request. An optional future MCP interface would call these same operations, not become a dependency of scheduled work. Playwright and intercepted payloads remain the primary capture path; alternative collectors are not required for v3.

## 3.4 Collection and processing

```
  slot or Collect now                     pending work in SQLite
          │                                         │
          ▼                                         ▼
  Feed.Cli collect                          Feed.Cli process
  browser lock while browsing               one processing lock per instance
          │                                         │
  save raw before parse                     recover unparsed captures
  close browser, sync profile               prepare ready posts and media
  export cookies, release browser lock      rolling judge/summary pool; videos
          │                                         │
  ingest under platform ingest lock         conditional result commits
  filter each new post before commit        (content revision must still match)
  download images, mark ready                        │
          │                                         ▼
          └──────── durable pending work ───► successive bounded selections
```

`collect` ends after capture and ingest; it never calls the model. Ingest runs even after a checkpoint or capture error, and commits deterministic filtering with each new post. Images are fetched promptly because signed URLs expire. An unfinished ingest remains recoverable from the raw store.

`process` runs independently of collection. It recovers unfinished ingest and feeds due verdicts and summaries through one rolling pool, with bounded preparation/results and interleaved media work. Automatic processing covers enabled platforms in one child, rotates selection fairly, and keeps requests flowing across batch and platform boundaries; it does not restart after each batch. An explicit `--limit` ends a finite scope. It needs neither a browser session nor a successful collect. A platform checkpoint blocks browser work, not processing of captured data. With the model disabled, ingest and permitted media work still run. Successful existing verdicts are not automatically re-scored; changed content invalidates the result for that content revision (chapter 5).

The scheduler launches both kinds of child process and reconciles durable state. SQLite rows are the work ledger; there is no in-memory-only queue and no broker. `reparse` repairs capture interpretation; `refilter`, `rescore` and `summarize` are explicit application operations exposed by the CLI. Chapter 12 defines scheduling and chapter 13 the commands.

## 3.5 The read path

```
   browser ──GET /?view=home&platform=all──►  Feed.Web
                                                │ read one validated configuration snapshot
                                                │ resolve view membership (chapter 10)
                                                │ query posts: visible, in view, newest first, render cap
                                                │ fold author bursts into stacks
                                                │ render the page
   browser ◄──────── HTML ──────────────────────┘

   browser ──POST /posts/{id}/like──►  Feed.Web writes a Likes row and a RunRequests row
   scheduler tick ──────────────────►  claims the request, spawns Feed.Cli like --platform X
```

The page render reads and never writes. Mutating actions use the documented HTTP POST routes and shared operations; their effects may be database rows or background-gallery files. Browser/model work is dispatched by the scheduler. Row counts and transaction batching are determined by each operation's contract, not by a universal one-or-two-row limit.

## 3.6 Processes, locks and the database

- The web host and CLI workers share SQLite in WAL mode, with a 30 second busy timeout. No transaction spans a browser call, model call or download. The live database requires a filesystem with supported local locking and shared-memory semantics (chapter 5).
- A browser lock per platform covers profile staging, browser use, sync-back and cookie export. It is released before ingest, model calls or video downloads. A slow judge cannot reserve a browser profile.
- An ingest lock per platform serializes capture interpretation and image registration. A separate instance-wide processing lock serializes automatic processing and explicit model commands, so `llm.parallel` is the total in-flight model-call limit for this instance. No operation holds a browser lock while waiting for either lock.
- Every execution has a durable run identity (pid and OS start time), including standalone commands. A run remains live after its browser lock is released. Reaping uses process identity, never absence of a browser lock as proof of death.
- Request claims and child outcomes survive a web-host restart. Pending hearts wait through re-login; uncertain attempted mutations fail for owner review instead of being repeated. Chapters 11 and 12 define the recovery rules.
- The web host finds the CLI executable next to itself and starts it with the same instance directory. The design is one local application, with process isolation for work that can crash or take time.

## 3.7 The instance is the product's memory

Everything personal lives in `data/`. The code carries no name, no handle, no category, no policy sentence. The owner's rules are three files:

| File | Holds | Chapter |
|---|---|---|
| `config.json` | platforms, filter gates/bypasses, close friends, schedule, model endpoint, retention, UI knobs | 4 |
| `taxonomy.json` | categories with definitions, and views | 4, 10 |
| `preferences.md` | keyword rules, muted people, always-show list, the policy the model reads, learned lines | 4, 8, 9 |

The web host observes and validates edits to the three files under the reload contract in chapter 4. An edit takes effect on the next request or scheduler tick. A load error keeps the last good files and shows on the debug page. Metadata checks, content signatures or reconciled file notifications may implement change detection.

## 3.8 Chapter map

| Chapter | Layer | Question it answers |
|---|---|---|
| 4 Instance and configuration | instance | What does the owner provide, and in what format? |
| 5 Data model | storage | What is stored, and what does each column mean? |
| 6 Collectors | capture | How does a post get from the platform to the raw store and the database? |
| 7 Media | files | Which files are downloaded, where, and when are they deleted? |
| 8 Filters | deterministic rules | Which posts are hidden without a model, and by whom? |
| 9 The language model | judge | How is a post scored, labeled and summarized? |
| 10 Views | reading | Which posts does a view show, and why? |
| 11 Like-back | the one write | How does a heart become a like? |
| 12 Scheduler and runs | timing | When does what run, and what stops two runs from colliding? |
| 13 Command line | operations | Which commands exist, and what does each one touch? |
| 14 Web host | HTTP | Which routes exist, and what does the UI get from each? |
| 15 User interface | the page | What does the owner see and do? |
| 16 Deployment and operations | environment | Where does it run, and how is it started, logged in and contained? |
| 17 Testing | verification | What must the tests pin, and what must be verified live? |
