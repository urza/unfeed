# 16. Deployment and operations

You are here: the environment. The chapters before this one described the software. This chapter specifies where it runs, what the machine needs, how it is built, started, logged in and contained, and how the operator keeps it running.

Source map: [build.sh](../build.sh) · [Dockerfile](../Dockerfile) · [compose.yaml](../compose.yaml) · [noVNC helper](../tools/novnc.sh) · [Runbook](RUNBOOK.md).

## 16.1 Where it runs

The application runs on one Linux host, directly or through the supplied Docker/Compose recipe. The web host and its CLI children share one private instance directory on storage with SQLite WAL and reliable local locking. A desktop, a server or an agent sandbox can host it; installation and port forwarding depend on that environment.

The checkout contains reusable code and synthetic examples. `data/` contains persistent personal state and is excluded from Git and Docker build contexts. Browser profiles are credentials; staging may use private temporary/cache storage outside the checkout. A container mounts the instance at `/data`; rebuilding an image does not replace that mounted data.

## 16.2 What the machine needs

| Tool | For |
|---|---|
| .NET 10 SDK (`global.json` pins 10.0.4xx, roll forward on feature) | build and run |
| the Playwright Chromium build, installed by `Feed.Cli browser install` and `browser install-deps` | the browser; it always matches the package version |
| `Xvfb` | the virtual display for the headed browser on a headless host |
| `x11vnc`, `websockify` with noVNC | the human's window into that display for the login |
| `ffmpeg` | reference processor for shrinking vision images; an equivalent implementation is allowed by 7.6 |
| `yt-dlp` | the video fallback download |
| File-copy support in .NET | profile staging uses the built-in recursive copy; rsync is not required |
| `sqlite3` | ad-hoc read-only queries by the operator |
| Noto fonts, including color emoji | the browser renders the sites' text and emoji as a human's browser would |

Baseline packages:

| Package | For |
|---|---|
| `Microsoft.EntityFrameworkCore.Sqlite` | the database |
| `Microsoft.EntityFrameworkCore.Design` | migrations, build time only |
| `Microsoft.Extensions.Logging` | the logging abstraction the file and console sinks implement |
| `Microsoft.Playwright` | collection in the CLI; local UI browser tests in the test project |
| `xunit`, `xunit.runner.visualstudio`, `Microsoft.NET.Test.Sdk` | tests only |

Use standard-library HTTP and JSON where straightforward. Maintained CLI parsing, logging or image-inspection libraries are allowed when they reduce custom infrastructure and meet the contracts in this spec. A model SDK or scheduling helper is optional, not required; it must preserve endpoint compatibility, durable recovery, bounded work and explicit operation scope. Record why each added dependency is needed in the repository, avoid overlapping packages, and keep IO behind application interfaces. No distributed broker or microservice deployment is required. Versions use central package management and checked-in lock files; locked restore rejects drift.

Razor Components come from the ASP.NET Core shared framework. Frontend development, build, tests and deployment use no separately installed Node.js, npm, Vite, TypeScript or other JavaScript toolchain. The plain JavaScript and CSS files are source assets served directly by ASP.NET Core. Browser automation and UI tests use Playwright for .NET and its managed installation; they do not require an npm project.

## 16.3 Build

`build.sh` publishes the web host and CLI into `out/`, so the web host finds the CLI next to itself. Both are framework-dependent. Publish order and MSBuild target organization are implementation choices; the final output must contain compatible dependencies and both runnable entry points. The build sets nullable and implicit usings on, treats the projects as one solution, and makes the test fixtures available to the tests.

The same .NET build compiles Razor Components and publishes the plain CSS, JavaScript modules and favicon with the web host, using coherent asset versioning (14.2). The web host hashes the deployed CSS/JavaScript tree at startup and uses that stamp in asset URLs, including relative module imports (14.2). No frontend install, transpilation or bundling command is part of build.sh. Local development uses the ASP.NET host, optionally through dotnet watch, rather than a separate frontend server.

```bash
dotnet build Feed.slnx -nologo -v q          # compile
dotnet test tests/Feed.Tests -nologo -v q --filter 'Category!=Browser' # fast suite
dotnet test tests/Feed.Tests -nologo -v q --filter 'Category=Browser'  # local UI browser checks
./build.sh                                    # publish web + cli into out/
```

## 16.4 Start and stop

After creating and validating the instance files:

```bash
dotnet out/Feed.Cli.dll init
dotnet out/Feed.Cli.dll rules
dotnet out/Feed.Web.dll
```

The default URL is `http://127.0.0.1:8000`; `/healthz` performs a database read. A foreground host stops with Ctrl-C. For unattended direct operation, use the host's process supervisor with the repository as working directory and an absolute `FEED_DATA` path. Identify a running instance by its command line and process identity before stopping it; do not kill every Feed.Web process by name.

The web host owns dispatch. Stopping it prevents new scheduled children but does not cancel existing CLI work. For maintenance, disable scheduling and let active work settle or terminate the specifically identified workers normally. Choose a different `web.port` when the default is occupied. Forward ports according to the deployment environment and bind beyond loopback only behind private access controls. Container bindings are described in 16.10.

## 16.5 Login through noVNC

One time per platform, and after any checkpoint:

1. `tools/novnc.sh` starts, idempotently: `Xvfb :99 -screen 0 1600x1000x24 -nolisten tcp`; `x11vnc -display :99 -forever -shared -nopw -localhost -rfbport 5900` (with the Wayland display variable unset, or it refuses to start); `websockify --web /usr/share/novnc 0.0.0.0:6901 localhost:5900`. `tools/novnc.sh stop` stops them. `FEED_DISPLAY`, `NOVNC_PORT` and `VNC_RFB_PORT` override the display, browser-facing port and loopback VNC backend port. Match `FEED_DISPLAY` to `browser.display`.
2. Publish port 6901 to the human's machine and open `http://127.0.0.1:6901/vnc.html?autoconnect=1&resize=scale`.
3. `dotnet out/Feed.Cli.dll login --platform facebook` (or `instagram`). The human logs in inside the noVNC window, two-factor included. The command waits up to 10 minutes for the feed.
4. `login <platform>: ok` clears the re-login flag. A timeout means a checkpoint is still on screen or the selectors drifted. Do not retry more than twice; report instead.

An existing closed Chromium profile imports with `profile import --platform X --from <dir>`. Verify its session; an imported expired session still needs login.

## 16.6 Logging

- Every process writes `logs/feed-<process>.log`: `feed-web.log` and `feed-cli.log`. The level comes from `ui.log_level` (info); the CLI's `--log-level` overrides it for that process. The web host always attaches the file sink, even with a console sink active.
- File line format: `yyyy-MM-dd HH:mm:ss` in UTC, a three-letter level (`INF`, `DBG`, `TRC`, `WRN`, `ERR`, `CRT`), the short category, `: message`. An exception adds ` | <type>: <message> | cause: <root cause>` when the root cause differs. Both sinks print the root cause of a wrapped exception.
- Rotation by size: at 5,000,000 bytes the file moves to `<file>.1`, replacing the old backup. One backup.
- The console sink writes to stderr with a local `HH:mm:ss`.
- Several CLI processes may append to the same file. The sink must coordinate append/rotation so concurrent processes do not corrupt lines or truncate another process's live output. A maintained sink or suitable file coordination may implement this; an in-process lock alone is not a correctness guarantee. Logging IO failures must not crash or change the outcome of the application operation.
- The framework's own categories are filtered to warnings.
- The debug page tails both logs. `GET /debug/log?file=cli|web` returns one or both.

## 16.7 Containment and secrets

- Network containment is a deployment responsibility. Needed destinations include the platforms/CDNs, selected model endpoint, optional background source and package feeds during setup. The application does not install a firewall.
- Chromium runs with its sandbox unless the environment needs `browser.no_sandbox`. A deliberate, narrow concession.
- The model API key sits in plain text in `config.json`, which is ignored by git. Browser sessions live in `profiles/`. No secret, name, handle or policy line exists in the source tree.
- Browser reads contact the platforms; media downloads contact their CDNs or use the platform permalink. Model requests send post text, policy/context and selected images to the configured primary/fallback endpoint. Bing backgrounds are optional external downloads. There is no application telemetry. The only supported platform mutation is the opt-in like.
- The web host has no authentication. Bind it to the loopback interface, or put it behind a private network. Serving the instance directory through the media route is a bug (chapter 14).
- The supplied image runs as a non-root user and Compose can use the host UID/GID. Host/container isolation and Chromium's renderer sandbox are separate boundaries. Additional container restrictions require testing against headed Chromium, display startup and writable staging. The recipe does not claim to install an egress proxy or authentication.

## 16.8 The runbook essentials

The full runbook lives in the repository for the operator. The essentials:

- Run every command from the repository root.
- After a parser fix: build, then `reparse`. A reparse never deletes raws.
- After a policy or a category definition edit: `rescore --all`, in slices with `--since` and `--limit` when the history is large.
- After a type, audience, tag-exception, keyword, mute or bypass edit: `rules`, then `refilter --all` to apply it to history.
- After a taxonomy rule edit (`close_friends_only`): `refilter --all`.
- After an interrupted collect: committed posts already have deterministic filters. Independent processing recovers unfinished ingest and model work without another collect, including during re-login. `process --platform X` drains due work by hand; add `--limit N` for a finite selection per stage. Automatic `process --platform all` keeps one rolling model pool across batches and rotates enabled platforms fairly. `reparse` repairs changed parser interpretation, and explicit `rescore`/`summarize` can drain history. Status separates capture, coverage and processing failures.
- During a long processing backlog: disable scheduling to prevent a replacement child, identify the current process through status (pid and OS start time), then use Ctrl-C or normal process termination and wait for lock release. Cancellation stops admission and cancels/observes active work; it does not require draining history. A new invocation reads the edited config, or an explicit command can now acquire the resource. Re-enable scheduling when ready. Existing item-attempt retry delays still apply; an intentional cancellation is not a dead-endpoint or unexpected-worker backoff.
- A friends import that reports `complete=false` adds friends but prunes none. Inspect its completion evidence and skipped-prune reason before repairing the adapter; an idle scroll is never permission to remove friends.
- After a web-host restart, the scheduler recovers requests from durable claims. Pending hearts waiting for login resume after `login`; a heart marked `outcome unknown` needs inspection of the original before another explicit attempt.
- Reading state: `status` first, then the debug page, then `sqlite3 -readonly data/feed.db` for anything else. Normal operation never writes to the database by hand and no routine command deletes posts. Reparse fixes interpretation prospectively; media prune keeps a post's folder while its row requires it.
- A historical parser defect can require an explicitly scoped, owner-authorized one-off data repair. For legacy carousel-slide rows, prove each id is a child in stored Instagram raw captures; missing dates/captions alone are not evidence. Stop conflicting workers, take a consistent SQLite backup with `.backup` (or the backup API), and print a dry run with row/media/file counts and dependent likes/feedback. Refuse automatic deletion when owner actions or files shared with retained posts need reconciliation. Delete only the reviewed, proven duplicate rows and their dependent media records in one transaction; after commit, remove only their exclusively owned files. Retain the backup and repair report. This exceptional repair does not become an automatic parser, reparse or retention deletion path.
- To browse an entire imported view, raise the instance's `ui.render_cap` as needed; 100000 is a supported example, not a new default. Measure page time/size and phone behavior before treating a large All page as inexpensive. No pagination or infinite scroll is added.
- Backgrounds: put personal images in `backgrounds/local/`; they join downloaded Bing images. Use `/manage/backgrounds` to pin, unpin, remove or choose another image. A pin persists across restarts and appears on the next page load; downloads run independently of page requests.
- Thumbs: `curate` reports them by author. Write what the owner meant as one sentence with the cited signals. Category adjustments and "show less" may be written without asking; a person mute or a new topic ban needs the owner's yes.
- Hearts on a new platform: turn `platforms.<p>.likeback` on, send one deliberate `like --post-id N` on a post of the owner's choosing, and check the result on the platform before leaving it on. With like-back off, the sender fails every queued heart without opening a browser.
- After a host rebuild: install the tools, build, start noVNC when a login is needed, start the web host. Browser sessions in the instance usually survive; a checkpoint means one login.


## 16.9 Initial setup and data durability

Start with a new private instance and every platform paused. Synthetic examples illustrate schema only; choose policies with the owner. The owner may work with an agent to translate preferences into instance files (4.7). Run init and rules, resolve unknown or ambiguous people, then enable the chosen platforms and model. Personal taxonomy, policy, endpoints and schedules remain outside the source tree. Defaults are a starting point, not the owner's final policy.

The live database stays on a filesystem supporting SQLite WAL. Back up it with SQLite's online backup API or a stopped/checkpointed database; copying feed.db alone while writers are active is not a complete backup. Include the three instance files and whichever raw/media history the owner wants to preserve. Protect profiles as credentials and restore them deliberately. Verify a restore into a separate paused instance before relying on a backup. Never run two scheduler hosts against one restored/live instance.

Bring-up validates a complete small path before a large import: capture and coverage, deterministic filtering, reading, independent processing, then restart/replay recovery. The application supports both platforms and the specified UI; this sequence limits how much state must be debugged at once.

## 16.10 Docker deployment

[Dockerfile](../Dockerfile) builds both executables with the pinned SDK and packages Chromium, display/media tools and the ASP.NET runtime. [compose.yaml](../compose.yaml) pulls `ghcr.io/urza/unfeed:latest` by default, mounts `./data:/data`, uses an init process, sets a restart policy, reserves shared memory and binds feed/noVNC ports to host loopback.

```bash
mkdir -p data
# Create the private instance files with scheduling and like-back disabled first.
export FEED_UID=$(id -u) FEED_GID=$(id -g)
docker compose pull
docker compose up -d
docker compose exec feed dotnet Feed.Cli.dll init
docker compose exec feed dotnet Feed.Cli.dll rules
docker compose exec feed tools/novnc.sh
docker compose exec feed dotnet Feed.Cli.dll login --platform facebook
```

[Publish Docker image](../.github/workflows/docker-publish.yml) builds and pushes a Linux AMD64 image on every push to `main` in `urza/unfeed`; it can also be run manually from `main`. Successful builds publish `latest`, `main` and `sha-<full-commit-sha>` tags. The workflow uses the automatic `GITHUB_TOKEN` with `packages: write`; no personal access token or repository secret is needed. OCI labels link the package to the source repository and revision. ARM64 images are not currently published.

After the first successful publish, the package owner must open the **unfeed package → Package settings → Change visibility → Public** to allow anonymous pulls. GitHub container packages initially default to private even for public repositories. If the package already exists, ensure this repository has Actions write access to it. Repository or organization policy must permit GitHub Actions to publish packages.

For a fixed deployment version, set `FEED_IMAGE=ghcr.io/urza/unfeed:sha-<full-commit-sha>` (or use `ghcr.io/urza/unfeed@sha256:<digest>` for an immutable reference). Export it for all Compose commands, or put it in an ignored `.env` alongside `FEED_UID` and `FEED_GID`. To upgrade, back up the instance and settle active workers, then run `docker compose pull` and `docker compose up -d`. Pulling alone does not restart the container.

To build locally instead of pulling a published image:

```bash
docker build -t feed-v3 .
export FEED_IMAGE=feed-v3
export FEED_UID=$(id -u) FEED_GID=$(id -g)
docker compose up -d --pull never
```

The login command waits for the human in noVNC. Use the same container CLI prefix for friends, collect, process and status. The image sets `FEED_DATA=/data` and binds the application to port 8000 inside the container; Compose publishes host loopback ports 8000 and 6901 by default. For remote login, tunnel those ports or use authenticated private access. To avoid port conflicts, keep a deployment-specific Compose override under `data/`, then pass it with `-f compose.yaml -f data/compose.override.yaml` on each invocation.

Chromium sandbox support depends on the host's user-namespace/security configuration. The default is on. If launch fails with `No usable sandbox`, either provide supported sandboxing or deliberately set `browser.no_sandbox` in the private instance after assessing that host. A container does not make this the same protection as Chromium's sandbox. Verify an actual headed launch; healthy web/noVNC routes alone do not prove browser support.

Stop noVNC when login work is done and no browser worker needs its display: `docker compose exec feed tools/novnc.sh stop`. `docker compose down` stops the deployment without deleting bind-mounted instance files. Before an upgrade, back up the instance, settle workers and pull the chosen image (or rebuild locally); do not run direct and container schedulers against the same data at once.
