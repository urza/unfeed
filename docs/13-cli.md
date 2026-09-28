# 13. Command line

You are here: the operator's surface. Every operation of chapters 6 to 12 is a subcommand of one console executable. This chapter lists them all with their options, scopes and exit codes. Commands support human and AI operators with explicit scopes, status output and result summaries.

Source map: [Program.cs](../src/Feed.Cli/Program.cs) · [Arguments.cs](../src/Feed.Cli/Arguments.cs) · [Reports.cs](../src/Feed.Core/Queries/Reports.cs).

## 13.1 Conventions

- One executable, one subcommand per invocation, exits when done. `dotnet out/Feed.Cli.dll <command> [options]`.
- Options are `--name value`, `--name=value`, or a bare `--switch`. Names ignore case. Bare words after the command are positionals (`raw prune`). A switch is on for `true`, `1` or `yes`. Dates parse as UTC.
- Global options on every command: `--data <dir>` (the instance directory), `--log-level <level>`, `--quiet` (warnings only on the console; the log file still gets everything). The instance is found through `--data`, then the `FEED_DATA` variable, then `./data`; the scheduler's children get only the variable, so the CLI must honor it. The `browser` command is the exception: it needs no instance and ignores them.
- Every command that mutates states its full scope: which posts, in what order, how many, from which end. Defaults are conservative. Every command prints one summary line: what it selected, how many rows, what it did.
- Exit codes: 0 success; 1 a failed operation or an unknown command; 2 a configuration error (printed as `config error: ...`) or an argument error (printed as its own message, such as `--platform is required` or `--friends-limit must be at least 1`); 75 deferred before work or while waiting for re-login (the request remains pending); 130 cancelled by Ctrl-C. Any other exception prints `fatal: <type>: <message> | cause: <root cause>` and the trace, and exits 1. A silent abort is a bug.
- Scheduler children accept internal `--request-id N --claim-token T` arguments together. Before work they register that claim and their run identity, then acquire only the resources they need, as specified in 12.8. A stale or incomplete claim cannot authorize work. Normal human invocations need neither argument.
- At start, every command except `browser` creates the instance layout, loads the three instance files, opens the log file `logs/feed-cli.log`, and migrates the database.
- The default scopes: reparse the newest feed-capture directory; refilter posts captured since the newest collect started; rescore/summarize ready rows without a result for the current content revision on enabled platforms, newest first. Refiltering all history or rejudging policy/configuration-stale verdicts requires --all on the applicable command. Process has its own bounded due-work scope.

## 13.2 Commands

| Command | What it does | Exit |
|---|---|---|
| `rules` | read-only report of effective type blocks, audience and tag exceptions, bypasses, resolved/unmatched/ambiguous people, categories and views. Validates all three files and explains precedence; never dumps secrets. | 0; 2 invalid instance |
| <code>process --platform X&#124;all [--limit N]</code> | independently drains due unfinished ingest, current-content verdicts, summaries and pending videos through successive batches of at most llm.batch. One rolling model pool spans batches. No default total limit; optional N (at least 1) caps selected units per stage across the requested platforms. `all` covers enabled platforms fairly; an explicit named platform may be paused. No browser; works during re-login or with the model off. | 0; 1 stage failure; 2 bad arguments; 75 lock busy |
| `init` | creates the instance layout and the database. Safe to repeat. Prints `init: instance at <root>, database <db> ready; config found|absent (defaults: every platform paused)`. | 0 |
| `status` | one screen from live config and the database: first the abandoned-run reaper with a `WARNING run #N ... closed as error` line per repair; per platform with posts, enabled or paused, post and visible counts, author and friend counts, the re-login state, the last finished run; one `WARNING timeline:` line per troubled close-friend profile with the note and the screenshot path; pending and claimed requests with child identity, waiting reason and recovery outcome, running runs by kind/phase, pending hearts; coverage by target and sweep cycle; processing counts, oldest pending age and next retry per stage; the model and endpoint or `disabled`, the unscored visible count, the category count, the policy line count; the disk line. | 0 |
| `login --platform X` | the one-time manual login through the headed browser (chapter 6). Refused when the lock is held. | 0 ok, 1 timeout or refused, 2 bad platform |
| `friends --platform X [--offline [--all-dirs]]` | imports the friends or following list with the prune guard (chapter 6). `--offline` reads the stored `friends-*` raws, the newest or all, and never prunes. | 0; 1 refused or no raws |
| `collect --platform X\|all [--mode M] [--scrolls N] [--person H] [--friends "A, B"] [--friends-limit N] [--retry-incomplete] [--trigger manual\|schedule]` | one run per platform (chapter 6). `all` means every enabled platform in registry order, and the summary names each result. The mode defaults to `home`; the URL aliases are normalized before the run row is written. A mode the platform does not declare is refused, never run as another mode. `--retry-incomplete` requires all_followed without person/friends overrides, selects due retries only and cannot start a new cycle. | 1 when any run ended `error` or `checkpoint` or a mode was refused; a `refused` run alone exits 0 |
| `reparse --platform X [--run <dir> | --path <dir> | --all | --failed] [--no-network]` | ingest/replay only, no model calls (chapter 6). Old --no-llm is an accepted no-op. `--failed` selects only undeleted failed snapshots for that platform, raw id ascending, excluding active browser runs; it bypasses parser-version holds and retry delay without replaying successful history. Records its own run and leaves due processing durable. | 0; 1 no run directory; 2 missing directory |
| `refilter [--post-id N | --since <date> | --all]` | re-runs current deterministic instance gates and category restrictions under ingest ownership. Use --all after a global rule edit (chapter 8). | 0 |
| `rescore [--all] [--platform X] [--limit N] [--since <date>] [--post-id N] [--author <ref>] [--text-only]` | the judge (chapter 9). Prints the counts and the endpoint that served. | 1 when any call failed; 2 model off, unknown platform or author |
| `summarize [--limit N]` | summaries for ready visible posts without a current-content summary, newest first, under the instance processing lock (chapter 9). | 1 when any call failed; 2 model off |
| `like [--platform X] [--post-id N]` | drains the like queue (chapter 11). `--post-id` queues one heart and sends it. Pending unattempted hearts survive re-login; uncertain attempted hearts fail for review. | 1 when any heart failed; 2 unknown post or platform; 75 deferred |
| `avatars --platform X` | backfills profile pictures offline from the stored friends raws. | 0 |
| `feedback [--limit N] [--uncurated]` | the newest thumbs signals with their posts, default 50. Reads only. | 0 |
| `curate [--write "<line>" --cites a,b [--author <ref>] [--approved] | --dismiss a,b]` | the curation loop over thumbs (chapter 9). Without flags, a report by author with the memory the model sees. | 2 missing cites, or a mute-like line without `--approved`; 1 unknown author |
| `raw prune [--before <date>] [--dry-run]` | raw retention by hand (chapter 7). Prints its scope and what it deleted. | 0; 2 without `prune` |
| `media prune [--dry-run]` | media retention by hand, then the disk measurement (chapter 7). | 0; 2 without `prune` |
| `browser install` / `browser install-deps` | installs the Playwright Chromium build, or its system packages. Runs Playwright's installer in-process and returns its exit code. | Playwright's code; 2 bad subcommand |
| `profile import --platform X --from <dir>` | copies an existing Chromium profile into the instance, caches and singleton files excluded, so an already logged-in browser needs no fresh login. | 0; 1 copy failed; 2 bad arguments |
| `help`, `--help` | the usage text | 0 |

## 13.3 Reading command output

Illustrative output uses synthetic counts; counters vary by operation and current live audience imports always report `complete=false`. Use `help` for accepted command syntax.

```
collect facebook home: ok run=#12 (found=31, new=9, scrolls=8, batches=6, media=14); ingest=done; coverage=home-only
process facebook: ok run=#13 (raws=0, scored=7, summarized=3, videos=1, failed=0, superseded=0, remaining=0)
friends instagram home: ok run=#14 (found=12, new=0, scrolls=4, batches=2, visited=0, incomplete=0, empty=0, unrendered=0); ingest=done; coverage=complete=false; pruned=0 [error=]
reparse facebook: ok (dirs=1, found=31, new=0, revised=0, media=0, pending=0)
login facebook: ok
```

Processing reports selected/completed/failed/superseded/selected-but-not-dispatched/remaining per platform and stage, including zero counts. Batch progress is reported while a long invocation is running; finishing a log batch does not drain the pool. `process --platform all` prints an aggregate summary and each platform's counts. Only an explicit `--limit` caps total selection per stage. Collection output reports capture and ingest separately and contains no model results.

In the timeline modes, `visited` counts the profiles opened, `empty` the profiles the site answered with zero posts, and `skipped` the profiles that rendered nothing within a minute. A run with found=0 and batches=0 is incomplete coverage, even if navigation succeeded. A site dialog is one possible cause; it is not proof that the owner's friends posted nothing.

## 13.4 What the operator never does

- No retry storms. A checkpoint or a refused run is a stop, not a loop.
- Never open a browser profile from two processes. The lock exists for that.
- Never delete `raw/` or `media/` by hand. Use the prune commands; the scheduler runs both daily.
- Never write to the database by hand. `sqlite3 -readonly` is for reading.
- Never put a name, a handle or a policy line into the source, the tests or the docs.
- Never answer a live question ("is X enabled", "how much data is there") from documentation. Use `status`, the debug page and the live config.
