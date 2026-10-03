# 5. Data model

You are here: the database. Chapter 4 described the files the owner writes. This chapter describes the one file the system writes: `feed.db`. Every later chapter reads or writes these tables. Chapter 6 fills them from captures, chapters 8 and 9 change visibility and verdicts, chapter 10 reads them for the page.

Source map: [Entities.cs](../src/Feed.Core/Domain/Entities.cs) · [FeedDb.cs](../src/Feed.Core/Infrastructure/FeedDb.cs) · [Migrations](../src/Feed.Core/Infrastructure/Migrations).

## 5.1 Database basics

- One SQLite file, `feed.db`, in the instance directory. EF Core with migrations. The schema is created and migrated at every process start.
- WAL journal mode, set at every start.
- Every connection sets a busy timeout of 30 seconds. Connections are pooled.
- **Current connection settings:** a 64 MB page cache, a 256 MB memory map, and temporary tables in memory. These are per-connection/resource choices, not correctness rules. Maintainers may tune them or use SQLite defaults with measured memory/latency evidence for the expected connection count and dataset. The live database must be on a filesystem that supports SQLite WAL shared memory and reliable local locking. A slow supported host mount is allowed; arbitrary network filesystems are not.
- Application timestamps are UTC, truncated to whole seconds, stored as text. OS process start times are the exception: retain the OS precision required to distinguish pid reuse (12.4).
- Three kinds of processes share the file: the web host, the scheduler inside it, and any number of CLI processes. Long writers batch their work. No transaction stays open across a browser call or a model call.

## 5.2 Tables

Column types are SQLite types. "?" marks a nullable column. Stored meanings, keys, uniqueness, referential actions and atomic state transitions are required. Nonunique index lists are a reference query plan: add, combine or replace those indexes using query-plan and workload evidence, while retaining required uniqueness and compatibility. EF entity layout, query formulation and tracked versus set-based updates are implementation choices. Schema changes still use migrations.

### Authors

One person or page on one platform.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Platform | text | `facebook` or `instagram` |
| PlatformAuthorId | text? | the proven raw author key from the capture; null for an author known only by a canonical profile URL. Never a shared placeholder such as `unknown`. |
| DisplayName | text? | |
| Url | text? | the profile URL |
| IsFriend | bool | the whitelist flag. Only the identity resolver sets it. Only the unfriend prune clears it. |
| AvatarPath | text? | relative path under the instance directory |
| RefsJson | text | a JSON array of this row's keys from AuthorKeys, sorted. A read cache, rewritten in the same transaction as the keys. |
| FirstSeenAt, LastSeenAt | datetime | |

Indexes: unique (Platform, PlatformAuthorId); (Platform, IsFriend).

### AuthorKeys

The alias table. It is the authority for "are these two keys one person".

| Column | Type | Meaning |
|---|---|---|
| Key | text, key | a ref such as `fb:123`, `fb:some.slug`, `ig:handle` |
| Platform | text | |
| AuthorId | integer | foreign key to Authors, cascade delete |

Index: (AuthorId). Only the identity resolver writes this table.

### Posts

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Platform | text | |
| PlatformPostId | text | unique per platform |
| AuthorId | integer? | foreign key, set null on delete |
| ObservedAuthorName, ObservedAuthorUrl | text? | author details from this post's capture, retained for display and model context when AuthorId is null; not identity or whitelist evidence |
| PostedAt | datetime? | null for ads and some suggested units |
| CapturedAt | datetime | set at insert, never changed |
| CreatedAt | datetime | |
| RawRef | text? | relative path of the raw file the row was first parsed from |
| LatestRawRef | text? | raw observation from which the current stored content was derived; distinct from first-capture provenance |
| MediaManifestJson | text | current ordered source media identities, kinds and fetch URLs from the parser; supports recovering missing slots without treating one successful image as a complete ingest |
| ContentRevision, ContentHash | integer, text | monotonic stored-content revision, starting at 1, and its SHA-256 hash (5.12) |
| IngestReadyAt | datetime? | set after this revision's image attempts and content hash are finalized. Null means ingest recovery is still needed; model work must wait. |
| Text | text? | the poster's own caption only |
| StoryTitle | text? | platform-generated context such as a cover/profile-photo update; separate from the caption |
| Permalink | text? | |
| LikeRef | text? | the platform handle like-back needs (Facebook feedback id, Instagram media pk) |
| IsSponsored, IsSuggested, IsReel, IsEvent | bool | story-type flags from the parser |
| MemoryLabel, MemoryText | text? | a memory reshare ("N Years Ago") |
| TagsJson | text? | a JSON list of `{name, url, kind}`. `kind` is `with` for an "is with" photo tag. Null when there are no tags. |
| SharedAuthor, SharedText, SharedUrl | text? | the shared content, flattened. See 5.8. |
| CategoriesJson | text? | latest stored category array; null means no verdict. It is current only when VerdictContentRevision matches ContentRevision. |
| LlmScore | integer? | 0 to 10 |
| LlmReason | text? | |
| PrefsVersion | text? | the verdict version stamp (chapter 9). Null means never scored. |
| VerdictContentRevision | integer? | the content revision the stored verdict judged; a mismatch means the verdict is stale because content changed |
| VerdictInputHash, VerdictModel, VerdictEndpoint | text? | exact prepared input hash, configured model id and serving endpoint, without credentials |
| JudgedAt | datetime? | successful verdict time |
| Summary | text? | latest summary; null means no result. Empty means too short for its recorded content revision. |
| SummaryContentRevision | integer? | content revision summarized or checked as too short |
| SummaryInputHash, SummaryModel, SummaryEndpoint | text? | summary provenance; model and endpoint null for the too-short marker |
| SummarizedAt | datetime? | successful summary or too-short decision time |
| LlmTokenLimit | integer? | last exhausted judgment budget for this content revision; null normally. Drives the bounded automatic budget ladder (9.9); cleared on success, changed content/identity, or explicit rescore dispatch. |
| LlmError, SummaryError | text? | latest task failure on this revision, cleared on success |
| LlmFailures, SummaryFailures | integer | consecutive task failures on this revision |
| LlmAttemptedAt, SummaryAttemptedAt | datetime? | persisted immediately before the respective model call; null means never attempted. Used for the retry delay and backlog order (chapter 9). |
| Hidden | bool | |
| HiddenBy | text? | the owner of the hide, from the closed set in 5.3 |
| HiddenReason | text? | |
| HiddenAt | datetime? | when the current hide began. Null while visible. |
| VisibilityRevision | integer | incremented by every hide/unhide change, including bulk changes; guards against an in-flight verdict overwriting a newer decision |

Indexes: unique (Platform, PlatformPostId); (PostedAt); (Hidden, PostedAt); (AuthorId).

Visibility updates have two shared semantics, named `SetHidden(owner, reason)` and `ClearHidden()` here. SetHidden sets `Hidden = true`, writes the owner and reason, and stamps `HiddenAt` only when the post was visible before. A second hide keeps the first date, even under a new owner. ClearHidden sets `Hidden = false` and nulls HiddenBy, HiddenReason and HiddenAt. Both increment VisibilityRevision. Entity helpers are a reference implementation; tracked and set-based updates must preserve the same atomic date/revision rules. After a bulk update, later decisions use fresh state rather than a stale tracked entity.

### Media

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| PostId | integer | foreign key, cascade delete |
| Kind | text | `image` or `video` |
| OriginalUrl | text? | the source URL. For a video fetched through yt-dlp it is the permalink. |
| Path | text? | relative path under the instance directory. Write-once. |
| Width, Height | integer? | read from the file header at download |
| Position | integer | the slot number, from 1. It equals the number in the file name. |
| SourceKey, ContentHash | text, text? | stable adapter media identity and SHA-256 of downloaded bytes; URL expiry tokens alone do not change identity |
| IsCurrent | bool | whether this row belongs to the current source manifest; retired slots remain for provenance but are not rendered or judged |
| AttemptedAt, DownloadAttempts, Error | datetime?, integer, text? | pending-video retry state; failed automatic attempts wait 30 minutes and stop after three failures |
| CreatedAt | datetime | |
| PrunedAt | datetime? | |

Index: (PostId).

Row states:

- `Path` set: the file exists.
- `Path` null and `PrunedAt` null: a pending video that waits for the verdict (chapter 7).
- `Path` null and `PrunedAt` set: retention deleted the file, or the video was never stored because the post was hidden. The row stays so that ingest never fetches it again.
- A failed image download leaves no row; the manifest and unfinished snapshot permit recovery of missing slots. A failed video keeps its pending row and retry state; after three failures, or a permanent size/tool limitation, stamp PrunedAt and keep the reason. It is not silently retried forever.

### RawSnapshots

One bookkeeping row per saved raw file.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Platform | text | |
| RunId | integer? | the run that captured it, no foreign key |
| Path | text | relative path `raw/<platform>/<run dir>/<file>` |
| Kind | text | `feed` or `friends` |
| CapturedAt | datetime | the file's last write time at registration |
| Parsed | bool | |
| AttemptedAt, ParsedAt | datetime? | ingest attempt and successful completion; a failed automatic attempt waits 30 minutes before retry |
| Error | text? | last ingest failure, cleared on success |
| Warning | text? | recognized nonfatal upstream warning, retained after successful parse |
| BlockedParserVersion | integer? | parser version that deterministically rejected these bytes; automatic replay waits for an upgrade |
| Deleted | bool | set by retention when the file is gone |
| Bytes | integer | |

Indexes: unique (Path); (Platform, CapturedAt).

### Runs

One row per work execution: collect, process, friends, reparse, rescore, summarize, refilter, like or login. Infrastructure-only commands such as help and browser installation do not create runs.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Platform | text? | one platform, or null for an instance-wide process run or multi-platform explicit model/refilter operation |
| Kind | text | the work execution kind above |
| Mode | text? | `home`, `close_friends` or `all_followed` for collect; null for other kinds |
| Phase | text | `starting`, `browser`, `ingest`, `processing` or `finished`; progress, not liveness authority |
| ProcessPid, ProcessStartedAt | integer, datetime | pid and OS start time at its available precision, recorded before work for every run, including standalone commands |
| RequestId | integer? | the scheduler request for this execution, when present; links recovery to the correct run |
| Trigger | text | `schedule` or `manual` |
| Status | text | see 5.3 |
| StartedAt, FinishedAt? | datetime | |
| PostsFound, PostsNew | integer | |
| StatsJson | text? | scrolls, batches, visited, skipped, empty, media, model calls, failures |
| Error | text? | |
| RawDir | text? | the run directory name under `raw/<platform>/` |

Indexes: (FinishedAt); (Platform, Status); (RequestId).

### RunRequests

Durable requests from page actions and automatic processing, claimed by the scheduler.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Kind | text | `collect`, `like`, `friends`, `login` or `process` |
| Platform | text? | required for collect/like/friends/login; null for an instance-wide process request |
| Mode | text? | for collect |
| RetryIncomplete | bool | collect request may only retry due targets in an existing sweep |
| PersonAuthorId, Person | integer?, text? | optional resolved person id and canonical profile URL for a bounded person collect; both supplied together, only for home mode without RetryIncomplete. Existing requests have null targets. Identity merges re-point the person id while retaining the requested URL. |
| Status | text | `pending`, `claimed`, `done`, `expired`, `refused` |
| RequestedAt, ClaimedAt?, FinishedAt? | datetime | |
| ClaimToken | text? | a fresh random token per claim, passed to the child; every child registration and completion must match it |
| ChildPid, ChildStartedAt | integer?, datetime? | registered by the child before work; the start time is the OS process start time, retained at its available precision to distinguish reused pids |
| ExitCode | integer? | the child's persisted result; committed with its terminal run and request state. A browser lock may already have been released. |
| Note | text? | why the row waits or was refused. The page shows it. |

Indexes: (Status, RequestedAt); unique (Kind, Platform) for active collect/like/friends/login requests; unique (Kind) for active process requests. Active means Status is `pending` or `claimed`. Require Platform for collect/like/friends/login and null for process. The separate process index enforces one active instance-wide request; SQLite null uniqueness must not permit duplicate process requests. Claims and queue insertion use transactions and these constraints, not a read-then-write guard alone.

### Feedback

One row per thumb press.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| PostId | integer | |
| Value | integer | +1 or -1 |
| At | datetime | |
| Platform, AuthorId | text?, integer? | |
| ViewKey | text? | the view the owner was in |
| Scope | text? | `live`, `hidden` or `unsorted` |
| CategoriesAtVote, ScoreAtVote, ReasonAtVote | text?, integer?, text? | the verdict at the moment of the vote |
| Curated | bool | set by the operator's curation (chapter 9) |

Index: (PostId).

### Likes

The like-back queue and the single like-state authority. No row means no like.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| PostId | integer | |
| Platform | text | |
| State | text | `pending`, `sent`, `failed` |
| Error | text? | |
| RequestedAt, SentAt? | datetime | |
| AttemptedAt | datetime? | committed immediately before a platform mutation. A pending row with this set after its sender dies has an uncertain outcome and becomes failed; it is never clicked again automatically. |

Indexes: (PostId, State); unique partial index on PostId where State = `pending`. Concurrent queue attempts reuse the existing pending row. The card reads the row with the highest Id for the post.

### PlatformStates

| Column | Type | Meaning |
|---|---|---|
| Platform | text, key | |
| NeedsRelogin | bool | set on a checkpoint, cleared by a successful login or friends run |
| LastOkRunAt | datetime? | set when a collect ends `ok` or `capped` |
| LastRunFinishedAt | datetime? | set when any collect finishes, including refused and failed collects. The manual collect cooldown clock; processing, likes and repairs do not reset it. |
| UpdatedAt | datetime | |

### TimelineVisits

One row per profile visit in a close-friends, all-followed or explicit person collect.

| Column | Type | Meaning |
|---|---|---|
| Id | integer, key | |
| Platform | text | |
| AuthorId | integer? | stable resolved author key for coverage and sweep progress; URL and name remain observation details |
| Url | text | observed profile URL without a trailing slash; resolved AuthorId is the stable key when available |
| Name | text | the display name at the time of the visit |
| RunId | integer | |
| At | datetime | |
| Status | text | `rendered`, `empty`, `unrendered` |
| CaptureStatus | text | `captured`, `empty`, `incomplete` or `failed`; rendering alone does not prove a usable capture |
| PostsFound, NewestPostedAt, OldestPostedAt | integer, datetime?, datetime? | observed distinct posts and their date range; never an assertion of full history coverage |
| StopReason | text | `depth`, `no_new`, `explicit_empty`, `checkpoint`, `timeout`, `capture_error` or `interrupted` |
| Note | text? | page title, final URL and the first 200 characters of text, for an unrendered visit |
| Screenshot | text? | published copy at `media/diagnostics/<platform>/<run dir>/timeline-<handle>.png`; original remains in the raw run directory |

Index: (Platform, Url, Id).

### SweepTargets

One resumable `all_followed` cycle per platform. The target list is a snapshot of resolved friend author ids in stable alphabetical order with AuthorId as the tie-breaker.

| Column | Type | Meaning |
|---|---|---|
| Platform, CycleId, AuthorId | text, text, integer | composite key; no personal names are part of the protocol |
| Position | integer | order within the cycle |
| State | text | `pending`, `visiting`, `retry`, `done`, `skipped` |
| RunId, VisitId | integer? | the claiming collect and its durable visit outcome |
| AttemptedAt | datetime? | when the visit began |
| Attempts, RetryAt | integer, datetime? | completed attempts in this cycle and next retry eligibility |

The active cycle id lives in Kv. A terminal visit and its target state commit together. A crash before that commit returns the target to pending once its run is dead. Failed and unrendered visits count as attempted for this cycle and remain visible as failures; they do not trap the sweep on one profile. Removed friends are skipped. New friends join the next cycle. A completed cycle remains for diagnosis until retention of sweep metadata is explicitly implemented; it does not drive feed selection.

### Kv

Small meta state.

| Key | Value |
|---|---|
| `last_visit` | ISO timestamp of the last "mark all read" press |
| `slot:<platform>:<mode>:<HH:mm>` | the local date `yyyy-MM-dd` on which the slot last fired |
| `maintenance:last` | the UTC date of the last daily maintenance run |
| `disk:summary`, `disk:at` | the last disk measurement line and its time |
| `sweep:<platform>:cycle` | active all-followed cycle id |
| `process:not_before` | next allowed automatic processing launch after unexpected worker/startup failure; no success delay between batches |
| `process:turn` | persisted next platform/model-task admission turn |
| `coverage:<platform>:not_before` | earliest next automatic capture-recovery launch after launch/startup failure |
| `model:<configuration hash>:<judge or summary>:not_before` | shared stage backoff after a wholly failed model batch; applies across platforms, without delaying ingest/media recovery |

The internal fairness cursor may be replaced by equivalent durable scheduling metadata under 12.9. Required retry markers, slot claims and owner-visible state keep their documented meanings; algorithm-specific cursor names are not a new public configuration interface.

## 5.3 Closed value sets

Every set below is a set of string constants. A value outside the set is a bug, and a test pins each set.

| Set | Values |
|---|---|
| Hide owner (`HiddenBy`) | `structural`, `whitelist`, `keyword`, `mute`, `llm`, `thumbs`, `other`. `thumbs` is reserved and never written. |
| Run status | `running`, `ok`, `capped`, `checkpoint`, `error`, `refused`, `imported` |
| Run trigger | `schedule`, `manual` |
| Run kind | `collect`, `process`, `friends`, `reparse`, `rescore`, `summarize`, `refilter`, `like`, `login` |
| Run phase | `starting`, `browser`, `ingest`, `processing`, `finished` |
| Request kind | `collect`, `like`, `friends`, `login`, `process` |
| Request status | `pending`, `claimed`, `done`, `expired`, `refused` |
| Like state | `pending`, `sent`, `failed` |
| Timeline visit status | `rendered`, `empty`, `unrendered` |
| Capture status | `captured`, `empty`, `incomplete`, `failed` |
| Sweep target state | `pending`, `visiting`, `retry`, `done`, `skipped` |
| Collect mode | `home`, `close_friends`, `all_followed`. The URL aliases `close-friends` and `all-followed` are normalized before storage. |
| Platform | `facebook`, `instagram`, in that registry order. Ref prefixes `fb`, `ig`. |

The run statuses mean:

- `ok`: the operation completed its selected scope; for home capture, it reached the no-new stop. It does not prove collection completeness.
- `capped`: it ended at the scroll cap.
- `checkpoint`: a login wall or a challenge stopped it.
- `error`: an exception or a dead process.
- `refused`: a lock or a re-login flag stopped it before the browser opened.
- `imported`: a reparse of a foreign directory.

## 5.4 Post lifecycle

Capture and prompt ingest run in the collect process. Model processing and pending videos run in an independent `process` child. That child can also recover unfinished ingest. Render runs in the web host and never writes.

| Stage | Writes | Re-run by |
|---|---|---|
| capture | raw files, RawSnapshots, Runs | a new collect run |
| parse | nothing; a parsed body becomes store input | `reparse` |
| store and deterministic filters | Posts, Authors, AuthorKeys, the hide triple; every new post is filtered in the insert transaction. `RawSnapshots.Parsed` is set only after the whole file ingests. | `reparse`; dedupe keeps it idempotent |
| type gates | the hide triple, owner `structural`, under configured blocked types and bypasses | `refilter` applies current instance settings |
| whitelist, keyword, mute, always-show | the hide triple, owners `whitelist`, `keyword`, `mute`, before a new post commits | `refilter` |
| images | Media rows and files | `reparse` |
| judge | CategoriesJson, LlmScore, LlmReason, PrefsVersion; the hide triple with owner `llm` under the threshold | `rescore` |
| summaries | Summary and revision/input/model provenance | `process` or `summarize`; changed content makes the old result stale |
| videos | pending Media rows filled, or stamped pruned | an independent processing pass |
| render | nothing; the "mark all read" action stamps one Kv row | every request |

Store rules:

- Dedupe on (Platform, PlatformPostId).
- A new row runs the configured type, audience, keyword and mute gates with effective always-show bypasses in its insert transaction. Type reasons are the matched type: sponsored, suggested, event or reel. The store and refilter use the same rule evaluator. An evaluation failure rolls back and leaves the raw for replay; no unchecked row commits. No network call occurs inside this transaction.
- An existing row gets a refresh of parser fields (author, time, text, flags, memory, tags, shared). Changed stored content advances ContentRevision as in 5.12. First RawRef and CapturedAt stay; LatestRawRef follows the current observation. Old model output is retained with its old revision for explanation, but is not presented as a current result.
- Ingest reconciles every expected slot with the current source manifest. Existing matching immutable files are reused; missing slots are attempted even when other slots succeeded. Removed/replaced source identities retire old rows. A pruned source identity is not automatically redownloaded.
- Ingest returns inserted and revised ids for counts. After image attempts, it finalizes ContentHash and IngestReadyAt; interruption before this point leaves durable recovery work. Processing selection uses readiness, missing/current-revision results and attempt times, not this invocation's ids. A media download failure is recorded but does not permanently block readiness.

## 5.5 Visibility

The triple `Hidden`, `HiddenBy`, `HiddenReason`, plus `HiddenAt`, is the single authority. Feed queries test `Hidden` and nothing else. No code parses `HiddenReason` to find the owner.

Reason formats: `keyword: <phrase>`, `muted: <entry>`, `llm <score>: <reason>`, `outside configured audience`, or the matched type name. Reasons explain decisions; code never parses them as state.

Who may change the triple:

- **The store**, at insert, through the shared deterministic evaluator for structural, whitelist, keyword and mute drops.
- **The filter engine** (chapter 8), for configured type, audience, keyword and mute gates. Refilter/content refresh may replace an older model hide with a current deterministic exclusion, release a deterministic hide whose rule no longer applies, or release a model hide through its configured bypass. Reserved thumbs/other hides stay until their documented override. With no applicable deterministic exclusion or shield, an old model hide remains.
- **The scorer** (chapter 9), for owner `llm`. A score under threshold hides unless a model shield applies. A fresh passing verdict releases a model hide. The scorer never re-judges another owner's hidden post, and conditional revision checks prevent it from overwriting a newer decision.
- **The unhide action** on the page clears the triple. The verdict columns stay, so a later rescore may hide the post again.

A stale policy hide stays hidden. Policy-only or model-configuration changes require explicit `rescore --all`; they do not automatically release or replace a verdict. A content revision change is different: it queues processing of the changed content, including an `llm`-hidden post. That hide remains until a passing fresh verdict releases it. No stale result is a reason to override another layer's hide.

## 5.6 Identity

Author identity is resolved during ingest through shared rules and persisted evidence. A pure resolver plus a store is the reference decomposition; all entry points preserve the same resolution and merge semantics. There is no separate identity sync pass later.

**Refs.** A ref is `<prefix>:<key>`, lowercased.

- From a URL. Facebook hosts are `facebook.com`, `fb.com` and their subdomains. `profile.php?id=N` gives `fb:N`. A numeric first path segment gives `fb:N`. Any other first segment gives `fb:<slug>`, unless the segment is a known non-profile word (`groups`, `photo`, `reel`, `watch`, `events`, and about forty more). Instagram gives `ig:<handle>` with a leading `@` removed, unless the first segment is a non-profile word (`p`, `reel`, `explore`, `stories`, `accounts`, and others). A scheme-less URL is retried with `https://`.
- From a platform author id: `<prefix>:<id>`.
- One observation yields every ref it can prove: the id ref and the URL ref.

**The resolver.** Given a platform, an author id, a URL, a display name, and optional flags:

If neither a valid author id nor a canonical profile URL yields a ref, return no author: store the post with AuthorId null and its observed name/URL. Do not create an Authors row, match by display name, or group unrelated observations under a placeholder. Such a post has no author-derived friend, close-friend, bypass or feedback status; configured tag exceptions can still apply. A later observation with proven refs may resolve it through normal ingest and content revision rules.

1. Look up AuthorKeys on that platform for any of the observation's refs.
2. When several authors match, they are one person. Keep the best row (friend flag first, then avatar present, then post count, then lowest id) and merge the others into it: re-point posts, feedback author refs, coverage visits, person collection requests and sweep targets; fill null fields; OR the friend flag; take earliest first-seen; move keys; delete merged rows; rebuild RefsJson. Duplicate targets within a sweep coalesce at the earliest position; a terminal visit keeps that target completed. Retain visit history and recompute the cycle's target total.
3. Otherwise match on exact (Platform, PlatformAuthorId), only when the observation has a valid author id. Never match null ids to each other.
4. Otherwise insert.
5. On update, a known value is never overwritten by a missing one. `IsFriend` is only ever set to true here. `LastSeenAt` moves forward. New refs are added unless the key exists anywhere. RefsJson is rewritten sorted.

**The friend list for matching.** Every friend row is exposed with its handle, its display name, its URL, and every key ever recorded for it as aliases. This is what lets a close-friend entry keep matching after the platform renames the handle. A friend whose URL yields no ref, even after the Instagram fallback, is left out and can match no entry.

**The unfriend prune.** Runs only after a live friends import with positive completion evidence as defined in 6.11, and at least one person. A no-new stop, a successful run status, and a nonempty result do not prove completeness. For every current friend on the platform, take every ref it is known by: all its keys plus the refs from its id and URL. When none of them appears in the proven-complete capture, clear `IsFriend`. An empty or unproven capture prunes nothing. Rows are never deleted. The prune touches one platform.

**The people matcher.** Shared pure matching rules used by close-friend resolution, mute rules and author views. The matching behavior below is required; helper decomposition is not.

- Name normalization: Unicode decomposition, strip combining marks, lowercase, collapse whitespace.
- Stemming: a token longer than five characters that ends in `ovi`, `ova` or `ovy` loses those three characters (Czech surname declension, so one entry covers both spouses).
- Token match: both tokens need at least three characters. The stems are equal, or one is a prefix of the other.
- Name match: every token of the query must hit some token of the name.
- Resolution: an exact handle or alias match comes first. The fallback is the name match. Several matches are all taken. Duplicates by URL are dropped. An entry with no match is reported as unmatched and never guessed.
- An Instagram friend without a URL gets `https://www.instagram.com/<id>` when the id is not numeric.

Author views resolve an entry with a colon as an exact AuthorKeys lookup, and a plain name through the name match over every author with a display name.

## 5.7 Runs, requests and locks in storage

- Every work execution starts running with its process identity. An operation refused before work may be recorded immediately as refused with FinishedAt. A foreign-directory reparse finishes imported after its work, not before it. A live execution is never represented by an already terminal provenance row.
- Finishing a run sets its status, phase `finished`, FinishedAt, counters, StatsJson and Error. Only a collect updates platform collection clocks: LastRunFinishedAt always, LastOkRunAt for `ok` and `capped`. Processing failures have their own run outcome and never turn a successful capture into a failed capture.
- A `running` row older than two minutes is abandoned only when its recorded pid and OS start time no longer identify a live process. A released browser lock proves nothing about worker liveness. The reaper closes a dead run as `error` with `abandoned: the process ended without closing the run (crash, kill or a full disk)`. The scheduler tick and status command both run it. Unknown process-liveness results are reported and do not authorize duplicate work.
- Claiming a pending request atomically sets `claimed`, `ClaimedAt` and a fresh `ClaimToken`, and clears old child identity and outcome fields. Only terminal states stamp `FinishedAt`; returning a claim to `pending` clears claim fields and leaves `RequestedAt` unchanged. Registration, completion and recovery compare the current token. Chapter 12 defines restart recovery and the startup grace period.
- Expiring requests reads before it writes, so a quiet tick opens no write transaction.

The lock file itself is described in chapter 12.

## 5.8 Shared content

Facebook renders the shared author's text and images while the poster's own caption may be empty. Judging the caption alone means judging nothing. So shared content is first-class, and flattened onto the resharing post:

- `Text` holds the poster's own caption only. The parser takes caption and shared text from different payload nodes. They never mix.
- `SharedAuthor` is a name string. `SharedText` is the shared body. `SharedUrl` is the shared item's link. There is no author row and no link to another post row for the shared item.
- A memory reshare has its own `MemoryLabel` and `MemoryText`.
- The display text of a post is the caption, a blank line, then the shared text. Summaries and the "long enough" test use the display text.
- Keyword rules match the caption and the shared text.
- The scoring prompt carries the shared block, so a share of a political article is political content whatever the caption says.

## 5.9 Dedupe and ordering

- Post dedupe key: (Platform, PlatformPostId). A friends import dedupes people by author id and URL. An Instagram capture dedupes identical bodies by SHA-256 before saving.
- Feed order: `PostedAt DESC, Id DESC`, then the render cap. SQLite sorts NULL last in a descending order on its own, so a plain column order keeps the (Hidden, PostedAt) index in use. Ads and some suggested units carry no timestamp and must never float to the top.
- Media in a card: `Position`, then `Id`.
- Pending requests: `Id` ascending, oldest first.
- Scoring and summary selection: explicit commands newest first; automatic bounded selection by oldest capture-or-attempt time, as in chapter 9.
- The header's "last collect": the newest finished run of kind `collect`. Processing has a separate status and never replaces collection freshness.

Every ordered operation has an explicit, reviewable ordering and tie-breaker. A named constant beside the query is one way to make that visible; SQL, LINQ and shared query helpers are equally valid. Tests pin result order, explicit newest-first selection, automatic order and retry delay.

## 5.10 Feed modes

The page query runs in one of three modes:

| Mode | Selection |
|---|---|
| live | `Hidden = false` and the view predicate (chapter 10); each view clause specifies whether a current-content verdict is required, so author clauses can admit unjudged posts |
| hidden | `Hidden = true`, for the debug inspection |
| unsorted | `Hidden = false` and no verdict for the current ContentRevision |

A platform filter applies when given, else the enabled platforms.

## 5.11 Performance budget

**Performance target:** a warm feed page render under 50 ms with 300 cards over a table of several thousand posts and media rows, measured by `X-Render-Ms`. State the hardware/filesystem, runtime/build, dataset, view complexity, warm-up and concurrency in measurements; report median and tail latency over repeated renders. This is a benchmark target on a documented environment, not a hardware-independent unit-test deadline. Investigate regressions on the same workload. Index/query design, caching and allocations may change to meet it while preserving current results, ordering and read-only rendering. Stored image dimensions, response compression and media caching support the separate browser loading budget.

That benchmark covers the default render budget, not a promise for a 100000-post page. `ui.render_cap` is the instance's finite budget; every query and render uses it without an additional 300-row clamp. Record representative large-view render time, HTML transfer size and phone behavior in private instance notes when tuning a real feed, or `measurements.md` for a synthetic benchmark, stating the cap, matching count and rendered count. Lazy images reduce immediate media transfer but not the HTML/DOM cost of a large All view. Live measurements and private instance settings are not fabricated or turned into code defaults.

## 5.12 Content and decision provenance

Captured facts and derived results have separate lifetimes. ContentHash is SHA-256 over a canonical serialization of the stored author identity and observed author fields, caption, platform story title when present, shared and memory fields, tags, post time, type flags, and ordered current media identities and known image-byte hashes. Signed-URL tokens, capture time, raw path, download attempt times and view definitions do not change content. Retention keeps byte hashes, so deleting a cached file does not by itself create a new content revision. The serializer and its version are fixed and tested. Identical replay does not advance ContentRevision. Adding or changing a stored story title advances the revision. A null story title preserves the prior hash serialization for posts without this context. UTC post timestamps use the same kind-independent serialization before and after SQLite round-trip, so a lost DateTimeKind cannot invalidate an unchanged post.

When those facts change, advance ContentRevision, clear IngestReadyAt and the model attempt times, and recompute deterministic visibility in the same transaction. A new deterministic exclusion can take ownership over an old `llm` hide; otherwise an old `llm` hide remains until a fresh verdict. This is the documented content-refresh path. Finish images and the content hash before setting IngestReadyAt. A newly recovered or replaced image changes the content revision when its known bytes change; eviction of an already known file does not. Ingest recovery completes an unfinished revision before model work can select it.

Keep the latest verdict and summary with their original content revisions and provenance. A mismatched verdict does not supply current category membership; a mismatched summary is not shown in the card. Debug output can show the older result explicitly as `content changed since judgment`. Content-stale results enter automatic processing; policy-only and model-only staleness require an explicit rescore. No event store or complete verdict history is required.

Preparation snapshots ContentRevision and VisibilityRevision. Applying a model result conditionally updates only if both still match and the row is still eligible. Otherwise discard the result, report `superseded`, and leave current work pending. A concurrent reparse, deterministic filter or owner action cannot be overwritten by an answer to an earlier state. No transaction spans the model call.

VerdictInputHash and SummaryInputHash describe the exact prepared messages, attached image-byte digests, model id and generation settings, without credentials. Store the serving endpoint and time separately, including fallback use. This records what was judged; temperature zero is not a reproducibility guarantee. Full private prompts need not be copied into logs. Any optional diagnostic prompt capture belongs only in the ignored instance directory.

## 5.13 Recovery diagnostics

Posts persist `LlmError`, `SummaryError` and consecutive `LlmFailures`/`SummaryFailures`. Failed results update these only for the prepared content and visibility revisions. Successful results clear their task's error/count; changed content clears both. Previous valid verdicts and summaries remain intact on failure. Attempt timestamps remain the retry authority. Older attempts without stored error details are labeled unknown or interrupted, never assigned a guessed cause.

RawSnapshots persist `Warning` for recognized nonfatal upstream faults and nullable `BlockedParserVersion` for deterministic parse failures. Such an immutable snapshot waits for a different parser version or an explicit reparse; transient file/network failures retain timed recovery. Parsed warning rows retain provenance and do not enter the retry queue.

SweepTargets persist `Attempts` and nullable `RetryAt`; `retry` is a waiting state in addition to pending/visiting/done/skipped. RunRequests persist `RetryIncomplete` so a resumed recovery claim cannot accidentally start a fresh sweep. Existing terminal visits are not rewritten during migration or raw replay.


The management queue migration expands the platform-request check constraint and active-request uniqueness to `friends` and `login`. The existing claim, startup identity and recovery columns apply unchanged. `management:refilter_pending` in Kv records unfinished management-driven refiltering; completion clears it in the same transaction as the post updates.
