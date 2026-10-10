# Capture classification and recovery plan

Status: implementation, automated verification and deployment preservation checks complete. Bounded actual-model checks pass supported isolated recovery and a previously rejected larger saved envelope after constraining evidence choices; advisory screenshot cases also pass. General capture compatibility is not established. Numbered specification chapters describe the implemented contracts; rollout evidence stays in private instance notes.

## Outcome

Reduce recurring false alarms from platform metadata while retaining evidence of missing posts and real collection failures. Repeated instances of one compatibility problem should produce one understandable issue group with affected-capture counts and inspectable evidence. Unknown responses must not become successful or informational solely to reduce the attention count.

## Findings before implementation

- `src/Feed.Cli/Site.cs` gates network requests, but returns only a boolean. Facebook matching is broad; Instagram has operation-name checks with legacy endpoint fallbacks.
- `src/Feed.Cli/Capture.cs` saves response bodies without preserving the request operation, source kind or a stable visit association alongside the bytes. Parsing happens both during capture and later during ingest.
- `PayloadParser.Embedded` extracts matching preload bodies but drops their enclosing preload identity. A label containing `Feed` is a selection hint, not proof that every nested response contains posts.
- `PayloadParser.Parse` handles posts, explicit-empty evidence, auxiliary recognition and diagnostics together. Its final no-post check covers the whole file; recognized posts in one root can mask an unfamiliar zero-post root. Mixed responses need explicit boundary tests.
- Auxiliary handling depends on narrow shape exceptions. These preserve safety but require repeated maintenance as new metadata variants appear.
- `RecoveryQuery` produces per-capture issues. `RecoveryPresentation` derives categories from message text, and the UI groups by platform/category, not by a stable compatibility cause.

The platform response format remains undocumented. This work reduces avoidable noise and improves diagnosis; it cannot guarantee permanent compatibility.

## Contracts to preserve

- Keep raw bytes immutable and replayable. No deletion, fabricated posts, guessed identities, or blanket suppression of unknown responses.
- Only the existing authoritative evidence can establish an empty timeline or complete coverage. Auxiliary responses never establish either.
- Retain independently usable posts from mixed responses, while keeping damaged or unknown post-bearing portions diagnostic.
- Preserve existing partial-observation and older-source protections, content revisions, valid verdicts, feedback, hearts and historical visit outcomes.
- Keep deterministic parser failures blocked until parser upgrade or explicit replay. Retain existing transient retry, scheduler pause, login and ownership rules.
- Existing captures without new context remain supported conservatively. Absence of context does not prove auxiliary content.
- Instance identifiers, payloads, response metadata and operational findings stay in ignored instance storage. Public tests are constructed synthetic examples.

## Implementation sequence

### 1. Preserve capture context without changing collection decisions

Introduce a small versioned capture-context record: source kind (network or embedded preload), platform operation/preload identifier when available, normalized endpoint family, response status/content type, collection/visit association when known, and context schema version. Store only selected fields; omit cookies, headers, tokens, query strings and full request bodies. Treat even selected context as private instance data.

Make network gating return a decision plus context. Make embedded extraction return the body plus its preload identifier. Thread context through capture-time parsing and later ingest. Handle asynchronous responses using the originating request's visit association; if association cannot be established, record it as unknown rather than assigning the current page.

Persist context in an immutable sidecar keyed to the raw filename, with a body hash; mirror queryable fields into nullable ledger columns as needed. Publish context before the atomic body rename, then register both. Discovery must recover a published body/context pair after a crash. Missing legacy context uses conservative fallback; malformed or mismatched context creates a visible diagnostic and cannot weaken validation. Retention removes associated sidecars under the existing ownership/eligibility rules. Same-body deduplication must not silently substitute context or lose visit evidence.

Deliver migration and crash-recovery tests first. This step keeps the accepted capture set and parser decisions unchanged.

### 2. Separate response classification from post extraction

Add an explicit classification pass per JSON/JSONL root and relevant envelope/branch, informed by context and structural evidence. Use typed outcomes for post-bearing content, recognized auxiliary content, explicit-empty evidence and unknown content. Keep content completeness, diagnostics and retry disposition separate from that classification; a mixed file need not have a single outcome.

Centralize supported operation families and structural rules in small platform adapters. A recognized profile operation can contain posts, so operation names alone cannot authorize ignoring its body. Recognize stable metadata boundaries rather than adding one exception per cosmetic field. Permit new fields only inside a proven auxiliary boundary; additions at content, identity, media or pagination boundaries remain diagnostic until supported. Errors on a post branch must remain errors even when nearby metadata is recognized.

Return structured diagnostics: stable code, platform/operation family, normalized structural path, affected scope, content impact, rule version and human-readable evidence. Preserve readable CLI messages. Extract and ingest safe observations using the existing rules; an unknown sibling/root must not disappear because another root yielded a valid post.

Audit existing auxiliary exceptions and migrate them into this model with positive and negative regression pairs. Explicitly preserve supported unavailable-post and partial-metadata behavior. Advance the parser version when the new behavior is ready for recovery.

### 3. Persist outcomes and group recovery by evidence

Persist current classification and structured diagnostics with parser/classifier versions and raw provenance. Prefer a child diagnostics table so mixed responses can retain several independent issues; do not force one reason onto an entire capture. Update diagnostic results atomically after each attempt and preserve existing ingest crash recovery.

Compute a versioned compatibility fingerprint from platform, operation family, diagnostic code and normalized structural path/shape. Exclude author/post identifiers, timestamps, signed URLs, array positions and free-form upstream messages. Keep impact and recovery action in the grouping key so warnings cannot merge with content failures. Treat a fingerprint as a shared failure signature, not proof of one underlying cause. Keep unknown legacy diagnostics conservatively separate until replay gives them structured evidence.

Expose groups with first/last observation, distinct affected captures and runs, representative evidence, impact, and the next useful action. Query summaries separately from paginated detail rows; do not render every repeated raw warning on the initial page. Preserve access to every retained diagnostic in Debug and existing collection links. Derive counts consistently for CLI, Manage and summary refresh; distinguish issue-group counts from capture counts and model-task counts.

Unrecognized content remains visible as collection compatibility work with possible coverage loss. Only positively recognized auxiliary material is informational or successfully processed. Repetition, elapsed time and low frequency never downgrade an issue automatically. A later verified clean same-person visit can supersede an associated active alert without clearing the saved error or historical gap.

### 4. Add a read-only replay comparison before enabling new decisions

Add a bounded CLI parser-audit operation over explicit capture IDs/runs or a limited failed selection. It must use the same parser as production, prohibit network/model calls and ingestion, and write a private machine-readable report of classification, diagnostics, post identities/content/media differences and explicit-empty evidence. Do not write audit results into live processing state.

Use a database copy for the ingestion rehearsal. Compare complete post/media/author/feedback/heart/coverage records, not just counts. Assert replay idempotence. For metadata-only cases, only reviewed capture diagnostics/processing state and replay-run bookkeeping may change. Any actual content correction must be reported and reviewed as part of the intended repair scope rather than silently restored or accepted.

Keep historical failures unchanged until an actual replay succeeds. Upgrading the schema must not mark them resolved. During deployment, pause dispatch by stopping supervision and drain workers before changing parser versions, so automatic recovery cannot race the bounded review.

### 5. Validate and roll out

Ship in reviewable stages: context persistence, classifier/structured diagnostics, recovery grouping, then the audit/replay tooling and rollout. Each stage must build and preserve existing contracts; do not deploy intermediate classifier behavior that lacks diagnostics or recovery support.

Required verification:

- Synthetic network/preload context, legacy/no-context paths, sidecar interruption/hash mismatch, deduplication and delayed response/visit association.
- Known auxiliary envelopes with varied cosmetic fields; unknown top-level fields; content hidden beside/inside metadata; unknown JSONL siblings; empty, malformed and truncated responses.
- GraphQL content versus decorative faults, missing author/media/pagination, real explicit-empty markers, and preservation of partial/unavailable content behavior.
- Grouping stability across IDs, positions, timestamps and message wording; separation of different impact/actions; accurate distinct counts, filtering, pagination and clearing after replay.
- Migration of old failures, blocked parser-version recovery, restart/idempotence, ownership, retention and offline replay with a network-rejecting handler.
- Relevant non-browser suite, Release CLI/Web builds and focused browser tests for Recovery with/without JavaScript, stable expanded details during refresh, and desktop/phone readability over bright busy wallpaper.

Audit a bounded private sample spanning both platforms, home/profile/embedded/deferred responses, clean captures and historical failures. Keep live evidence private; synthetic tests do not prove general platform compatibility. Take an online SQLite backup plus application/settings backups, rehearse on an isolated copy, deploy under ownership locks and run only the reviewed offline replay. Confirm health, classification backlog, group/capture counts, unchanged settings and preserved historical data before resuming scheduling. Inspect subsequent ordinary scheduled runs for recurrence and missed-content indicators; do not collect extra data merely to generate a green status.

Update affected numbered chapters (data model, collectors, scheduler/recovery, CLI, UI and testing; retention/deployment where changed), the runbook and implementation map alongside code. Regenerate `docs/spec.html` after numbered chapter edits. Document rollback for the additive schema and preserve the pre-deployment database backup; never restore an old database over newer user activity without reconciliation.

## Acceptance criteria

1. Supported metadata variants stop generating parser-blocked tasks, without creating posts or empty-coverage claims.
2. Unknown or damaged post-bearing branches remain visible, including beside valid posts or metadata.
3. Repeated instances show a stable issue group with accurate affected-capture counts and full accessible provenance; unrelated content errors remain distinct.
4. Old raw files, crash recovery and parser-version retry rules continue to work without inferred context.
5. Bounded audit and rehearsal demonstrate preservation of existing content, media, actions and coverage for auxiliary-only repairs.
6. Recovery remains usable with a large synthetic backlog through bounded summaries and paginated details.
7. Tests, builds, documentation and deployment-specific checks pass, with any remaining platform limitations stated explicitly.
