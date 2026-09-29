# 12. Scheduler and runs

You are here: timing and ownership. Browser collection and processing are independent CLI jobs. The scheduler in the web host dispatches them from durable SQLite state; it does no browser or model work in-process.

Source map: [Scheduler.cs](../src/Feed.Web/Scheduler.cs) · [Actions.cs](../src/Feed.Core/Application/Actions.cs) · [Ownership.cs](../src/Feed.Core/Infrastructure/Ownership.cs) · [TimelineCoverage.cs](../src/Feed.Core/Application/TimelineCoverage.cs).

## 12.1 Shape

- One hosted background service, one tick every 5 seconds, one scheduler host per instance.
- Children are `collect --platform X --mode M`, `like --platform X`, or `process --platform all`. Automatic processing has no per-batch invocation limit. Slots supply `--trigger schedule`; manual collect requests supply `--trigger manual`.
- Children receive FEED_DATA and the web host's working directory. Resolve the CLI through FEED_CLI, then beside the host, then the development build tree up to six parents up. A DLL runs through dotnet. The resolved command appears in debug output.
- Launch errors and tick failures are recorded with their reason. A missing CLI fails a manual request visibly. Automatic processing launch failures wait 30 minutes before another attempt, not every tick.
- The scheduler evaluates due work with indexed queries. It never reimplements the filter or model selection rules; those are shared application operations.

## 12.2 One tick, in order

Before these steps, refresh the validated instance snapshot as in 4.5, even when scheduling was disabled on the previous tick. Use that snapshot throughout this tick.

1. Reconcile durable claims and worker outcomes, including children launched by an earlier host (12.8). Reap dead runs by process identity, not browser-lock absence.
2. Expire pending collect requests older than `run_request_ttl_minutes`. Like and process requests do not use this TTL. Repair state even when scheduling is disabled.
3. If `scheduler.enabled` is false, stop. Existing workers may finish, but start no new work or maintenance.
4. Serve manual collect requests oldest first. Refuse unknown platform/mode or a re-login requirement. Wait for collection eligibility and the platform's manual cooldown; then claim and launch.
5. Reconcile pending Likes into one active like request per platform. A busy browser makes it wait. Like-back off fails pending unattempted hearts with the reason. A re-login requirement leaves the request pending. Otherwise launch the sender. No collect cooldown applies. Processing never blocks the browser gate.
6. Fire eligible slots (12.3).
7. Dispatch due retries from an existing all-followed cycle (12.10), then processing (12.9). This gate is independent of browser checkpoints, browser locks and collect cooldowns.
8. Run daily maintenance (12.5).

**Browser busy** means the platform's browser lock is held, or a claimed collect/like child is still starting its browser phase. A collect in `ingest` and any processing/model command do not make the browser busy. A registered child's phase is read from its run; before registration, a browser request reserves startup only during the two minute grace.

**Collection eligible** additionally means no active collect for that platform, even if its browser has already closed. This avoids overlapping collection ingests while still allowing a heart or login during ingest. The page's collect guards use this definition. A held ingest lock does not by itself block a like.

**Processing busy** means the instance processing lock is held or a process request is claimed within startup grace or has a live child. At most one automatic process worker is dispatched across the instance. Explicit model commands take the same lock.

**Cooldown** is `manual_cooldown_minutes` since the last finished collect on that platform, including failed/refused collects. Only manual collect requests wait for it. Model work, repair commands and hearts do not reset it.

## 12.3 Slots

Collection times are local-time slots in the instance config, per platform and mode:

```json
"platforms": {
  "facebook": {
    "schedule": { "home": ["07:30", "13:30"], "close_friends": ["20:00"] },
    "schedule_days": { "all_followed": ["sat", "sun"] }
  }
}
```

- "Now" is the home zone: the `timezone` config key, else the process zone. The browser fingerprint uses the same zone.
- For each enabled platform and each mode in its schedule:
  - normalize the mode key; the URL aliases are accepted;
  - skip an unknown mode;
  - when `schedule_days` lists days for the mode, today's three-letter weekday prefix must match one of them;
  - when `schedule_intervals` supplies `{ "days": N, "start_date": "yyyy-MM-dd" }` for the mode, today must be the start date or a non-negative multiple of N calendar days later. This uses local calendar dates, including across month/year boundaries and daylight-saving changes, not elapsed 24-hour periods. Weekday restrictions and interval restrictions intersect;
  - parse each `HH:mm` and skip one that does not parse.
- The fire time is today at the slot plus the jitter. `jitter_minutes` (10) shifts the slot by 0 to N minutes. The shift is deterministic per platform, mode, slot and day and survives a process restart; 0 disables it. The reference hash starts at 17, folds the characters of `platform|mode|slot|yyyy-MM-dd` with `h = h * 31 + char`, then normalizes modulo N + 1. Define overflow/negative handling if using that recipe. Another stable algorithm is allowed; process-randomized runtime string hashing is not. Keep the chosen algorithm stable or version its transition so an upgrade cannot refire a recorded slot. Every tick agrees on the same day's fire time.
- A slot fires when all of these hold:
  - it has not fired today: the `Kv` marker `slot:<platform>:<mode>:<HH:mm>` is not today's local date;
  - now is at or past the fire time;
  - now is within 30 minutes after the fire time;
  - the platform has no re-login flag;
  - the platform is eligible to collect (12.2).
- Collection eligibility and re-login are checked before the marker is set, so the slot retries on later ticks inside its window.
- A slot whose window passed while the app was down is skipped. There are no catch-up bursts.
- Firing sets the marker and starts the collect child with `--trigger schedule`. Slots ignore the manual cooldown.
- The debug page lists every slot with its jittered fire time, whether its calendar restrictions allow it today, and whether it fired today.

For an every-other-day sweep, configure `schedule_intervals.all_followed` with `days: 2` and an explicit first local date. The fixed anchor survives host restart and downtime; missed dates do not move the cadence or create catch-up runs. Each eligible slot still performs one bounded sweep invocation. Set `sweep_limit` large enough to cover the desired list, or provide enough slots to finish its batches. An interval does not automatically drain the whole cycle, and due retries from an existing cycle can still run between scheduled dates (12.10).

## 12.4 Resource locks and run identity

Locks use exclusive files with pid, OS process start time and acquisition time. A dead identity is stale immediately; a reused pid is not the old owner. An unreadable identity has a one hour grace. Stale deletion and acquisition retry once. Disposal removes only the owner's lock.

- `locks/<platform>.lock`: profile staging, browser use, browser close, sync-back and cookie export. Release before ingest, model calls or video downloads. Collect, like, friends and login use it; profile import also requires it.
- `locks/ingest-<platform>.lock`: ingest, replay, image registration and explicit deterministic refilter on that platform. All callers use the same application operation. Automatic processing skips a busy ingest stage without consuming an attempt; it may still process previously ready posts.
- `locks/processing.lock`: automatic process workers, rescore and summarize. It enforces one instance-wide model budget; each worker uses `llm.parallel` within that budget. No browser lock is taken for model work or yt-dlp.

Acquire processing before ingest when both are needed. Never wait for either while holding a browser lock. An explicit command that cannot acquire its required lock reports deferred (75); it does not start a second worker. Scheduled work waits for a later tick.

Every work run records its own process identity before work. A live collect remains live in its ingest phase after releasing the browser lock. A dead run is reaped after the two minute grace. OS start times retain enough precision to distinguish pid reuse. Failure to inspect a process is an observable uncertainty, not proof that it died.

## 12.5 Daily maintenance

On the first tick of each UTC day, mark `maintenance:last`, then run raw retention, media retention and disk measurement. Maintenance never removes raws from a live capture/ingest, unparsed captures, or media currently being prepared or downloaded. File deletion coordinates with ingest/processing ownership; if busy, defer that portion and record the reason. It never takes a browser lock to wait for a model.

## 12.6 Requests from the page

- `POST /collect` inserts a home request for each enabled platform without an active collect request/run. A claimed request counts as active.
- `POST /collect/{platform}/{mode}` inserts one under the same guard. Normalize mode aliases; unknown modes answer 404.
- A heart press atomically queues its Likes row and ensures one active like request.
- Unique active-request constraints and conditional claim updates prevent duplicate dispatch. UI state is derived from durable requests and runs, never just the current host's child list.

## 12.7 Collection boundary

The collect child captures, closes and syncs the browser, exports cookies and releases the browser lock, then ingests under the ingest lock. It persists its own collection result and exits without model calls. If ingest cannot finish, raw snapshots remain pending for the independent worker. A model outage never changes the outcome of a successful capture. Collection and processing counts are reported separately.

## 12.8 Durable claims and restart recovery

1. Atomically claim a pending request with ClaimedAt and a fresh ClaimToken. Pass `--request-id N --claim-token T` to the child. The child conditionally registers its pid/start time and creates its Runs row in the same transaction, before any work or resource lock use. A stale token or already registered claim cannot authorize work.
2. Acquire the resource lock for the operation. If a gate changed, return the matching claim to pending with a reason and exit 75. Keep RequestedAt. Terminal work commits its run outcome, ExitCode and request state together, even if its browser lock was released earlier. A parent never overwrites a committed outcome.
3. On restart, a registered live process keeps its claim; observe its durable progress and outcome. Do not launch a replacement merely because the parent-child relationship was lost. Check both pid and OS start time.
4. An unregistered claim has two minutes to start. After that, atomically invalidate its token and return it to pending, or expire a collect whose original TTL has elapsed. A late child with the old token exits without work. Like/process requests remain durable without a collect TTL.
5. A registered dead child with no outcome has its run closed as error and claim refused. For collect, do not repeat browser capture automatically; unparsed raws still enter processing recovery. For process, due rows remain pending and failed/interrupted task attempts respect their retry delay. For like, wait for browser ownership to be free, fail attempted-but-unconfirmed hearts as outcome unknown, and preserve unattempted hearts for a fresh request.
6. A launch/startup failure observed by the parent refuses that claim. It fails unattempted hearts with the startup reason, avoiding a loop on a missing executable. Automatic processing records `process:not_before` 30 minutes ahead for the instance; its rows remain pending. Deferred lock contention is not a failure and consumes no task attempt.

Recovery can repair state while the scheduler is disabled but launches nothing. A successful process exit never marks individual hearts sent. Only confirmed Likes outcomes do that. Standalone runs use the same process identities, locks and recovery rules without a request row.

## 12.9 Independent processing

For each enabled platform, the scheduler queries due unparsed snapshots, unfinished ingest, verdicts, summaries and videos (chapters 6, 7 and 9). If any exist, ensure one active instance-wide process request with Platform null. On host startup, also request recovery for all enabled platforms to register complete raw files saved just before a crash but not yet registered in SQLite. Coalesce this into the same request. This scan runs in the child, not in the render or scheduler tick.

Launch at most one processing child across the instance, using `process --platform all` without `--limit`. It owns the processing lock until its invocation ends and uses one rolling pool for all model calls. `llm.batch` bounds each selection and its accounting; it does not end the child or drain the pool. Continue selecting while due work remains. No scheduler tick, sleep or process restart lies between model selection batches. The child exits when all eligible work in its configuration snapshot is exhausted, deferred or stopped, all admitted operations are settled, and no usable input remains. It does not wait for future retry times or future captures. New due work is discovered by later ticks if it arrives after that final check.

**Required fairness.** Every continuously eligible platform and model task receives selection opportunities within a documented finite number of admission turns, independent of another platform's backlog size. Restart must not continually reset service to the same first platform/task. A turn admits at most `llm.batch` rows for a platform/task in its documented order. Move to another eligible turn without waiting for the previous batch's requests to finish; keep all calls within the shared `llm.parallel` budget. A large judgment backlog cannot starve summaries or another platform. Recheck a post's summary/video eligibility against current visibility; a late model answer cannot overwrite an intervening hide.

**Current scheduling.** `Processing` rotates a persisted `process:turn` cursor through platform/judge/summary pairs, skipping empty or backed-off tasks. With two platforms, each continuously eligible model task has an opportunity within four admission turns. Raw recovery rotates platform batches independently. Weighted or other fair selection is allowed when its service bound and restart behavior are documented and tested; persist equivalent scheduling state if needed. Batch/platform boundaries remain accounting boundaries, not drain barriers.

Recover raws/unfinished ingest and process permitted videos in bounded turns as well, at most `llm.batch` selected units per stage and platform per turn. Interleave those turns with model feeding and result application so neither recovery nor model work starves. Slow file/network work holds no database transaction, never shares a context concurrently, and does not block dispatch of already prepared model inputs. A raw file is the atomic recovery unit; its post count is reported separately. Complete each ingest before setting its ready marker. Exclude units already selected during the invocation; failures do not loop within the same child. A busy ingest lock skips that stage without consuming an attempt, while previously ready model work can continue.

An explicit `process --platform X|all --limit N` selects at most N units per stage across its requested platforms, in batches no larger than `llm.batch`. This optional limit counts selections, not successes, and cannot be refilled after failures or superseded results. It deliberately ends a finite scope; the automatic scheduler does not supply it. An explicit named platform may be paused; `all` means platforms enabled in the child's startup configuration. Workers read configuration once at startup, and a scheduler disable prevents new children while an existing invocation settles its work.

**Owner control during a backlog.** A running child is cancellable without draining its backlog: Ctrl-C and the host's normal termination signal enter the cleanup path in 9.5, stop new dispatch and release ownership after started work has been canceled/observed. Persist a distinguishable terminal cancellation outcome through the existing run/request lifecycle, so intentional shutdown is not mistaken for an abandoned child. Status reports the actual pid and OS start time so an operator can identify it safely. To apply new configuration or give an explicit command the model resource, the supported sequence is disable automatic scheduling, cancel the current processing child, wait for ownership release, then start the desired command with a fresh snapshot; re-enable scheduling afterwards. Disabling scheduling alone does not retroactively cancel a child. Cancellation is not an endpoint failure or a 30-minute instance failure backoff; actual stamped item attempts retain their retry rules. No periodic restart at every batch is introduced. A future convenience control may wrap this sequence without changing its ownership rules.

Failed item attempts wait 30 minutes before a later invocation. A wholly failed model batch closes admission/dispatch for that task and model configuration and writes its shared 30 minute backoff across platforms; chapter 9 defines buffered and in-flight handling. Raw/media recovery and unaffected tasks remain eligible. An unexpected worker failure not already covered by item/task retry state delays another automatic launch across the instance by 30 minutes using `process:not_before`. No eligible work means no child; if only ingest is due and its lock is held, wait instead of launching empty workers. Model-disabled invocations still recover raws, ingest/filter and process permitted media. A re-login flag does not block any of this; processing does not open a browser or export cookies.

SQLite readiness and attempt columns are the durable work ledger. No generic distributed queue is required. In-memory reservation and channels only coordinate the live owner; a crash leaves unstamped work due and stamped attempts subject to retry delay, including a crash between dispatch authorization and sending HTTP. The worker reports per-platform, per-stage selected, completed, failed, superseded, selected-but-not-dispatched and remaining counts throughout the run and at completion. The debug page distinguishes waiting for a model, retry delay, processing disabled and browser re-login, and shows active HTTP requests and prepared/unapplied queue counts.

## 12.10 Bounded capture recovery

After manual requests and scheduled slots, and once that cycle has no pending/visiting first-pass targets, dispatch due `retry` targets from the current all-followed cycle using a durable collect request with `RetryIncomplete=true`. A child receives `--retry-incomplete` and cannot create a new cycle. Chapter 6 defines the three-attempt budget, one/two-hour delays, first-pass precedence and stop conditions. A one-hour persisted `coverage:<platform>:not_before` launch gate also prevents loops on a missing executable or failed startup. Scheduling disabled launches none of this work. Normal configured slots remain the authority for starting new cycles.

Processing due checks exclude raw snapshots blocked on the current parser version; an upgraded parser makes them eligible again subject to the existing item delay. Debug exposes blocked raws and nonfatal upstream warnings separately from timed retries.

Judgment due-work selection shares the worker's token-retry predicate: respect `llm.token_retry_delay_minutes`, dispatch only when a larger permitted budget remains, and do not launch solely for exhausted token-retry rows. Debug and CLI reports use the same eligibility rules (9.9).
