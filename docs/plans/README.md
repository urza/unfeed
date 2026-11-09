# Plans

Standalone implementation plans from the step-back review of 2026-11-09. Each plan is self-contained so that a fresh session can review it without the review conversation. No plan is approved yet. Review a plan, record decisions on its open questions inside the file, then set its status line before implementation starts.

| Plan | Finding | Depends on |
|---|---|---|
| [01 Parser fail-open, remove model recovery](01-parser-fail-open.md) | The parser fails closed; a fifth of the code copes with that | none |
| [02 Derived visibility, prompt-only verdict version](02-derived-visibility.md) | The verdict version includes inputs the model never sees | none |
| [03 Media identity from platform ids](03-media-identity.md) | Media identity follows the CDN URL and drives re-judging | none |
| [04 Author identity with id-only merges](04-author-identity.md) | Identity merges on a blocklist, destructively, with no log | none |
| [08 Storage lifecycle and bounded read path](08-storage-lifecycle.md) | Storage and the read path have no lifecycle plan | 02 for the category write helper |
| [09 Judge-quality loop](09-judge-quality-loop.md) | The learning loop the UI promises does not exist | 02 recommended first |
| [10 Stop silent feature failure](10-silent-failures.md) | Features fail silently | 02 removes video context from the verdict version |

Numbers match the review's ranking. Findings 5, 6 and 7 (security posture, checkpoint detection, cross-process coordination) have no plan here yet.

Every plan records live-instance counts from 2026-11-09 as aggregates only. No name, handle, URL or post text appears.
