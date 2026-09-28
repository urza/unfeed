# 2. Overview

You are here: the objective, the scope and the binding rules. The vision (chapter 1) said why. This chapter defines the supported scope and its boundaries. The architecture (chapter 3) then shows the parts.

Source map: [Configuration and validation](../src/Feed.Core/Domain/Configuration.cs) · [Code map and limitations](../IMPLEMENTATION.md).

## 2.1 Objective

### Who

- **The owner.** One person with Facebook and Instagram accounts. The owner reads the page, writes the rules, and presses the hearts.
- **The operator.** A language-model agent that can run the system for the owner. It builds, starts, collects, repairs and reports. It talks to the owner in chat. It works in the deployment environment with access to the checkout and private instance. A human can do the same work with the same commands.
- **The maintainer.** Someone changing the implementation while preserving or deliberately updating its documented contracts.

### What problem

The owner wants to read what friends post without the platform's junk and ranking. The platforms give no API for that. The owner also wants a feed sorted by personal categories, with a plain reason behind every decision.

### What success looks like

- The owner opens one private page and reads the friends' posts of the last day, newest first, in the owner's own views. The owner is done in minutes.
- Posts excluded by the owner's configured type, audience or content rules do not reach the live page. Platform flags are facts; which types to exclude is instance policy.
- Every post on the page can say why it is there. Every hidden post can say who hid it and why.
- A rule change is a text edit. New judgments use it; the operator explicitly refilters or rescores history when the owner wants earlier decisions updated.
- A platform payload change is an afternoon repair. The raw captures are on disk. The operator fixes the parser and replays them.
- The operator answers every "what is happening" question from the page, the status command and the logs. It never needs SQL archaeology.
- A fresh clone with an empty instance builds, starts, and shows an empty feed.

## 2.2 Scope

### In scope

- Facebook and Instagram, through a real browser session logged in as the owner.
- Three collect modes per platform: the home feed, the close friends' timelines, and a sweep over every followed timeline.
- A raw store of every captured response, a replay command, and independently recoverable processing.
- Coverage records for visited profiles and a resumable sweep, without claiming that captured posts are the platform's complete history.
- A friends/following whitelist imported from the platform. Removal requires proven completeness; current live adapters are add-only (6.11).
- A filter stack: configurable type and audience gates, keyword rules, muted people, explicit always-show bypasses, and a language-model judge.
- Categories and views defined in instance files. Five view shapes: category, author list, category-and-author intersection, rare voices and union.
- Summaries of long posts.
- Local media: prompt image ingest, independent video processing after judgment, and configurable retention that limits retained media.
- A scheduler with local-time slots, manual "collect now" requests, per-platform locks and cooldowns.
- Like-back: a heart on the page becomes a like on the original post.
- Thumbs up and down as recorded feedback that feeds the model's memory and the operator's curation.
- A private web page with the feed, the views, the actions, and an operator debug page.
- A command-line tool for every operation, with a one-line summary per command.
- One instance directory that holds everything personal.

### Out of scope

- Other platforms. The adapter contract keeps the door open. This specification ships two adapters.
- Comments, direct messages, posts, follows, or any write other than a like.
- Real-time updates, push, or notifications.
- Multi-user, accounts, cloud sync, telemetry.
- Managed hosting, public multi-tenant service and automatic infrastructure provisioning. Direct Linux and the supplied Docker deployment are supported.
- Automatic filtering on thumbs. Thumbs are recorded and used as words. No threshold hides a post.

### Non-goals

- Not a general scraper. The capture is bounded to the owner's own feeds and friends.
- Not an engagement product. No streaks, no badges, no counters about the owner.
- Not a replacement of the platforms' full feature set. Stories, live video and messaging stay in the apps.

## 2.3 Binding rules

These rules hold across every chapter and define the verification obligations in chapter 17. When a chapter and a rule disagree, the rule wins. Test coverage and deployment evidence are recorded separately; a written requirement is not a claim that every live scenario has been exercised.

1. **No personal data in version control.** No name, handle, category name, close-friend list or policy sentence exists in the committed source tree. All of it lives in the instance directory, which is ignored by git. The repository ships one sanitized example instance with fake names for the tests.
2. **Explicit operation scope.** Every mutating command states its full scope: which posts, in what order, how many, from which end. Defaults are conservative. Every command prints one summary line with what it selected and what it did.
3. **One scheduler owns work dispatch.** A web action never starts a browser or a collect run. It writes a request row. The scheduler claims the row and starts a child process. Background-image refresh is an independent hosted background task and never starts browser or model work.
4. **Single visibility authority.** Three columns on the post row decide visibility: hidden, hidden-by, hidden-reason. Feed queries never re-apply filter logic. Hide ownership changes only through the documented ingest/refilter, fresh-verdict and owner-unhide paths.
5. **Identity resolved at insert.** Shared identity rules decide the author identity when the observation lands. Keys are namespaced per platform. There is no identity sync pass after insert. All ingest paths use the same semantics; this does not prescribe a class count.
6. **Pending work is independent of collection.** One processing worker feeds successive bounded selections through a rolling model pool, without draining at batch boundaries. It rotates fairly across enabled platforms and model tasks, and runs without another collect and during browser re-login waits. Failures remain eligible for a later invocation after a retry delay. Rejudging an existing result for a changed policy or model remains an explicit command. An explicit limit bounds selected scope; batch size does not cap an automatic invocation.
7. **Raw before parse.** Every intercepted response is written to disk before any parser reads it. A broken parser is fixed and replayed. A lost capture is gone forever.
8. **The feed render never writes.** A page render reads database projections and one validated instance-configuration snapshot. Application writes in the web host belong to action routes and the documented background tasks. Browser and model work happens in command-line processes. Background-image downloads run off the request path (14.6); page and image GET routes only read local assets and never fetch Bing.
9. **The operator interface supports a language model or a human.** Every command and every action route is self-describing. A runbook lives in the repository. All state lives under the project folder. Documentation describes defaults, never live state.
10. **Per-platform locks and cooldowns.** One lock file per platform covers collect, like, friends import and login, because all four open the same browser profile. A manual cooldown is per platform too.
11. **Thumbs record, never filter.** A thumb writes a feedback row. No filter reads it. The scoring prompt reads it as sentences about the author, and the operator curates it into policy lines. A thumb never mutes a person.
12. **Fail-open on judgment, enforce configured gates before publication.** A model failure does not create a hide. The owner's deterministic gates apply before a post commits, and excluded posts are not judged while hidden by those gates. The owner can change those rules; no personal category or exclusion is baked into the parser.
13. **Filter before publication.** A new post and its deterministic filter result commit in one transaction, including after a checkpoint or capture error. No unchecked post becomes visible between ingest and a later pipeline stage.
14. **Prove completeness before pruning.** An idle scroll is not proof that a friends list is complete. Without positive completion evidence, an import adds friends and removes none.
15. **Recover durable requests.** Request ownership and child outcomes survive a web-host restart. Pending hearts resume after re-login; a platform write with an uncertain outcome is never repeated automatically.

## 2.4 Glossary of the main terms

- **Instance.** The directory with the owner's data: config, taxonomy, preferences, database, profiles, media, raws, logs.
- **Platform.** Facebook or Instagram. Keys: `facebook`, `instagram`.
- **Mode.** A capture surface: `home`, `close_friends`, `all_followed`.
- **Run.** One CLI work execution with its kind, process identity, phase and outcome in the database.
- **Coverage.** Evidence of which surfaces were visited and what was captured; never a guarantee that every platform post was found.
- **Raw.** One intercepted response body, saved to disk before parsing.
- **Post.** One captured unit from a feed or timeline, stored once per platform post id.
- **Author.** The person or page that posted. One row per platform and platform author id.
- **Whitelist.** The set of authors flagged as friends (Facebook) or followed (Instagram).
- **Close friends.** The owner's own list of people per platform, from the instance config.
- **Verdict.** The model's answer for one post: a score, a reason and category keys.
- **Category.** A label the model assigns. Defined in the taxonomy file.
- **View.** A named selection of posts the page can show. Defined in the taxonomy file.
- **Unsorted.** Visible posts without a judgment for their current content revision.
- **Owner (of a hide).** The layer that hid a post: `structural`, `whitelist`, `keyword`, `mute`, `llm`, `thumbs`, `other`.
- **Stack.** A burst of one author's posts folded into one card on the page.
- **Like-back.** The heart on the page that becomes a like on the platform.

## 2.5 Contracts and implementation notes

This specification separates stable behavior from replaceable implementation choices:

| Kind | What it means | Examples |
|---|---|---|
| **Required contract** | Preserve the stated behavior, bounds and compatibility. Unqualified requirements in the chapters belong here. | Owner policy and visibility, ordering and scope, durable recovery, no duplicate/uncertain platform writes, continuous model feeding, the chosen stack, config keys/defaults, CLI and HTTP interfaces, stored-data meaning, and the specified UI. |
| **Implementation note** | The current mechanism, or an explicitly labeled alternative. It explains the code without making every internal detail a compatibility requirement. | Queue/coordinator design, parser traversal, query formulation, helper/class layout, cache implementation and scheduling algorithm. |
| **Tuning guidance** | A documented starting point or measured target, qualified by workload and environment. Changes need relevant evidence, not a different product contract. | SQLite cache allocation, internal polling/debounce mechanics, and benchmark methodology. Public configuration defaults and safety/scope limits remain contracts unless explicitly labeled otherwise. |

Observed site selectors, endpoints and payload fields are the compatibility baseline for the supplied captures, not a promise that external sites never change. Adapt them using captured evidence, sanitized regression fixtures and the required live checks. Keep the meaning of a post, identity, audience, post-heart target, checkpoint and completeness evidence. A new parser or selector must not reintroduce the documented failure on an older supported fixture.

Maintainers can change algorithms, internal APIs, data structures, query plans, helper libraries within the dependency policy, and source organization where no contract fixes them. Use the simplest design that meets the contracts. Performance improvements must preserve correctness, owner control, recovery and scope; tests observe those outcomes rather than requiring a particular call graph. Record material departures from a reference design, the reason and validation in the implementation description or repository notes. Routine implementation choices do not require owner approval.

This freedom does not authorize a new stack, altered filter semantics, extra platform writes, changed public defaults, lost compatibility or a different UI. Such a proposal changes the requirements and must be identified as such. A method name used to explain an internal operation is not a required C# symbol unless it is explicitly part of an external interface. Documented database/file formats and versioned hashes remain compatible; storage changes use migrations or explicit version handling.

Keep the useful specificity: a concrete reference algorithm and a regression example help a maintainer understand a requirement. Where an alternative is permitted, test the shared invariant. For example, later model batches must start while an earlier slow request is still running, regardless of whether the implementation uses Channels or another bounded async design.
