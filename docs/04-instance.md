# 4. Instance and configuration

You are here: the files the owner provides. Chapter 3 placed the instance directory at the center of the picture. This chapter specifies every file in it and every key the code reads. Chapter 5 specifies the database that lives next to these files.

Source map: [Configuration.cs](../src/Feed.Core/Domain/Configuration.cs) · [InstanceFiles.cs](../src/Feed.Core/Infrastructure/InstanceFiles.cs) · [ConfigurationTests](../tests/Feed.Tests/ConfigurationTests.cs).

## 4.1 The instance directory

Everything personal lives in one directory. The code finds it in this order:

1. The `--data <dir>` argument of the command or the web host.
2. The `FEED_DATA` environment variable.
3. `./data` under the current directory.

The path is made absolute. The layout:

```
data/
  config.json            all settings (4.2)
  taxonomy.json          categories and views (4.3)
  preferences.md         rules and the policy the model reads (4.4)
  feed.db                the SQLite database, with its -wal and -shm files (chapter 5)
  profiles/<platform>/   the Chromium profile, plus cookies.txt exported for yt-dlp
  media/<platform>/<post id>/NN.ext      post images and videos (chapter 7)
  media/avatars/<platform>/<author id>.ext
  raw/<platform>/<run dir>/NNN.json|jsonl  raw captures and timeline screenshots (chapter 6)
  logs/feed-web.log, feed-cli.log          size-rotated logs (chapter 16)
  locks/<platform>.lock                    browser ownership (chapter 12)
  locks/ingest-<platform>.lock              ingest ownership
  locks/processing.lock                     instance-wide processing/model ownership
  backgrounds/                             cached page backgrounds (chapter 14)
  backgrounds/local/                       the owner's background images, in the same pool
  backgrounds/pinned.txt                   selected background public name, when pinned
  backgrounds/removed.txt                  reference storage for removed Bing identities
```

Every instance-using process creates the root and the folders `profiles`, `media`, `raw`, `logs`, `locks`, `backgrounds` at start when they are missing. The operation is idempotent. Nothing creates the three text files. The owner writes them, or copies them from the example instance.

The instance directory is ignored by git. The repository ships one example instance with fake names under the tests. A fresh clone with an empty instance directory runs and shows an empty feed. `data/SETUP.md` is the place for private setup/handoff notes; diagnostics and backups also stay under `data/`. An alternative `--data` path inside a checkout needs its own Git and Docker exclusions.

The directory may sit on a slow host mount only when that filesystem supports SQLite WAL and the required process locks. Arbitrary network filesystems are not supported for the live database; cache pragmas do not make them safe. Browser profile staging remains useful on a supported but slow mount (chapter 6).

## 4.2 config.json

Format rules:

- JSON with `//` comments and trailing commas allowed. Humans edit this file.
- Keys are `snake_case`. Key matching ignores case.
- An unknown key at any level is a load error, never a silent no-op.
- A missing file gives the defaults: every platform paused, the model off. A fresh clone runs.
- A malformed file is a load error. The CLI exits with code 2 and prints the error. The web host keeps the last good file and shows the error on the debug page.
- Validate shape, closed enum values and operation bounds: batch, parallel, sweep_limit, render_cap and coverage_stale_hours must be at least 1; retention/cooldown/size values and backgrounds.blur_px cannot be negative; numeric values must be finite; delay minimum cannot exceed maximum; threshold is 0 to 10; schedule slots must be valid HH:mm. Unknown timezone keeps the documented fallback but is reported by `rules`, so the owner can correct it before enabling schedules.

The complete key list. Every key is optional. Defaults are in the table and are required when a key is absent. An owner may override configurable values. Implementation tuning does not silently change public defaults or introduce unrecognized configuration keys.

### Root keys

| Key | Type | Default | Meaning |
|---|---|---|---|
| `timezone` | string | unset | IANA id such as `Europe/Prague`. One clock for scheduler slots, the browser fingerprint and the debug page. Unset or unknown means the process zone. The process zone can be wrong: a POSIX `TZ` string like `CEST-2` maps to UTC in .NET. Set this key. |
| `platforms` | map | `{}` | one block per platform key. A platform absent from the map is paused. |
| `llm` | object | | the model, see below |
| `filters` | object | | deterministic owner choices, see below |
| `scheduler` | object | | see below |
| `raw_retention_days` | int | 60 | raw files older than this are deleted daily. 0 means never. |
| `hidden_media_retention_days` | int | 7 | every file of a post hidden longer than this is deleted. 0 means never. |
| `hidden_video_retention_days` | int | 1 | the video of a hidden post is deleted after this shorter grace. 0 means never. |
| `video_retention_days` | int | 180 | videos of posts older than this, by post date, are deleted. 0 means never. |
| `max_video_mb` | int | 50 | a video above this size is not downloaded. 0 means no cap. |
| `ui` | object | | see below |
| `backgrounds` | object | | see below |
| `web` | object | | see below |
| `browser` | object | | see below |
| `likeback` | object | | see below |

### `platforms.<platform>`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | true, once the block exists | a paused platform keeps its stored posts, its view by URL and its commands. It leaves the default feed, the toolbar, the scoring scope and the schedule. |
| `close_friends` | list of strings | `[]` | handles (the pinned form) or display names, resolved by the people matcher (chapter 5) |
| `sweep_limit` | int | 25 | maximum profiles visited by one all-followed run; at least 1. Each run resumes the saved cycle. `--friends-limit` overrides this run's budget. |
| `schedule` | map of mode to list of `HH:mm` | `{}` | local-time slots per collect mode |
| `schedule_days` | map of mode to list of weekday prefixes | `{}` | `mon` to `sun`. An absent or empty list means every day. |
| `likeback` | bool | false | sending hearts is opt-in per platform. Where it is off the page offers no heart. |
| `self_username` | string | unset | Instagram only: the owner's handle, used when the following import cannot read it from the page |

### `llm`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | false | |
| `base_url` | string | `""` | an OpenAI-compatible chat completions base, for example `http://host:8000/v1` |
| `api_key` | string | `""` | sent as a bearer token when non-empty |
| `model` | string | `""` | |
| `fallback_base_url`, `fallback_api_key` | string | `""` | a second endpoint tried once per failed call (chapter 9) |
| `vision` | bool | true | attach the post's images to the verdict call |
| `vision_max_images` | int | 3 | images per post sent to the model |
| `max_tokens` | int | 4000 | the verdict budget. Reasoning models spend tokens on thinking first. |
| `summary_max_tokens` | int | 4000 | the summary budget |
| `summary_enable_thinking` | bool? | null | Optional vLLM/Qwen chat-template override for summaries only. Null omits the extension; false disables thinking when supported by the configured endpoint(s). Judgment requests are unchanged. |
| `summary_languages` | list | `["English", "Czech", "Slovak"]` | a summary keeps the post's language only for these. Any other post is summarized in English. Empty means always English. |
| `threshold` | int | 4 | the hide line. A score under it hides the post. 0 hides nothing and still assigns categories. |
| `timeout_seconds` | int | 180 | per call, minimum 10 |
| `batch` | int | 20 | maximum rows per selection/accounting batch; automatic and explicit model work feed successive batches through one rolling pool. Not an invocation limit or an execution barrier; use `--limit` for a hard scope cap. |
| `parallel` | int | 1 | maximum model calls in flight across this instance, including judgment, summary and explicit commands. Refill free slots promptly while prepared work remains, across batch boundaries. Tune for the endpoint; it is not multiplied by platforms or workers. |

### `scheduler`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `enabled` | bool | true | when false the tick still reaps abandoned runs and expires requests, and nothing else: no slots, no manual runs, no like runs, no processing workers, no daily maintenance |
| `jitter_minutes` | int | 10 | each slot fires 0 to N minutes late, deterministic per slot and day |
| `manual_cooldown_minutes` | int | 30 | a manual request waits this long after the platform's last finished run |
| `run_request_ttl_minutes` | int | 60 | a pending collect request older than this expires; like requests wait across re-login without this TTL |

### `filters`

The parser reports facts. These settings decide which facts exclude a post. Defaults are starter choices, not unchangeable product policy. Unknown values are configuration errors.

| Key | Type | Default | Meaning |
|---|---|---|---|
| `blocked_types` | list | `["sponsored", "suggested", "event"]` | any of `sponsored`, `suggested`, `event`, `reel`; empty blocks no types. A reel is not inherently unwanted. |
| `audience` | string | `"friends_and_followed"` | `friends_and_followed` requires the platform whitelist; `all_captured` permits any captured author, without adding new collection surfaces. Facebook friends and Instagram followed accounts define the whitelist; a followed Facebook page is not a Facebook friend. |
| `friend_tag_exception` | string | `"with"` | under a restricted audience: `none`, `with` (only photo-context tags), or `any` (any parsed tag whose identity resolves to a whitelisted author). It never overrides a blocked type by itself. |
| `always_show_bypasses` | list | `["keyword", "llm"]` | which gates a matched always-show entry bypasses: any of `types`, `audience`, `keyword`, `llm`. Mutes remain explicit exclusions and must be removed to allow a muted author. |

An author view is a selection, not an implicit override. To guarantee inclusion despite model policy, the owner or setup agent writes a matching always-show entry and configures the desired bypasses. The effective order is type rules, audience, keywords, mutes, then the model. An allow statement in model prose cannot undo an earlier deterministic exclusion. All of these choices can be explained without reading source code.

### `ui`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `render_cap` | int | 300 | finite per-instance budget of posts on one page, at least 1. May be raised, for example to 100000, to show an entire matching view when it fits. No separate hard-coded 300 cap, paging or infinite scroll. |
| `coverage_stale_hours` | int | 48 | after this many hours without a successful timeline capture/explicit empty response, coverage is shown as not checked recently; at least 1 |
| `default_view` | string | `"all"` | the view the bare page opens. An unknown key falls back to `all`. |
| `log_level` | string | `"info"` | `trace`, `debug`, `info`, `warn`, `error` |
| `stack.min_posts` | int | 3 | an author's burst of this many posts folds into one card. 0 turns folding off. |
| `stack.window_days` | int | 7 | the burst window |

### `backgrounds`, `web`, `browser`, `likeback`

| Key | Type | Default | Meaning |
|---|---|---|---|
| `backgrounds.source` | string | `"bing"` | `bing` enables a daily background download of eight archive days across eight markets (14.6). Other values disable downloads. Existing cached images and local photos still form one pool. |
| `backgrounds.blur_px` | number | 4 | non-negative blur radius in CSS pixels, applied only to the background image layer; 0 turns it off |
| `web.host` | string | `"127.0.0.1"` | read once at start |
| `web.port` | int | 8000 | read once at start |
| `browser.stage_profiles` | bool | true | copy the profile to local disk before each browser run |
| `browser.stage_dir` | string | unset | the staging root. Fallbacks: `FEED_STAGE_DIR`, then the user cache directory plus `feed/profiles/<platform>`. |
| `browser.display` | string | `":99"` | the X display to start when `DISPLAY` is unset |
| `browser.no_sandbox` | bool | false | run Chromium without its sandbox. `FEED_NO_SANDBOX=1` does the same. |
| `likeback.min_delay_seconds` | number | 2 | the human pause before each heart |
| `likeback.max_delay_seconds` | number | 10 | |

Illustrative configuration with synthetic identities; choose settings with the owner before enabling schedules or like-back:

```json
{
  // one clock for slots and the browser
  "timezone": "Europe/Prague",
  "platforms": {
    "facebook": {
      "enabled": true,
      "close_friends": ["some.handle", "Firstname Lastname"],
      "schedule": { "home": ["07:30", "13:30"], "close_friends": ["20:00"] },
      "schedule_days": { "all_followed": ["sat", "sun"] },
      "likeback": true
    },
    "instagram": {
      "enabled": true,
      "close_friends": ["handle1", "handle2"],
      "schedule": { "home": ["08:00"] },
      "self_username": "owner.handle"
    }
  },
  "llm": {
    "enabled": true,
    "base_url": "http://127.0.0.1:8080/v1",
    "model": "local-model",
    "parallel": 4
  }
}
```

## 4.3 taxonomy.json

The taxonomy holds the categories the model assigns and the views the page offers. Format rules are the same as for `config.json`: comments and trailing commas allowed, unknown keys are an error. A missing file means no categories and no views.

```json
{
  "categories": [
    {
      "key": "personal",
      "label": "Personal",
      "definition": "the author's own life: family, holidays, photos the author took",
      "default": false,
      "close_friends_only": ["instagram"]
    },
    { "key": "interesting", "label": "Interesting", "definition": "noteworthy shared or found content worth reading", "default": true },
    { "key": "funny", "label": "Funny", "definition": "jokes, memes, funny stories or images" }
  ],
  "views": [
    { "key": "personal", "label": "Personal", "category": "personal" },
    { "key": "family", "label": "Family", "authors": ["fb:some.handle", "Firstname Lastname"] },
    { "key": "family_updates", "label": "Family updates", "category": "personal", "authors": ["fb:some.handle"] },
    { "key": "rare", "label": "Rare voices", "rare": { "max_posts": 3, "window_days": 90 } },
    { "key": "home", "label": "Home", "union": ["family", "personal", "rare"] }
  ]
}
```

### Categories

| Field | Meaning |
|---|---|
| `key` | the protocol value. It appears in the model's reply, in the stored label array and in view definitions. A stable slug: `[a-z0-9_-]+`. |
| `label` | display text |
| `definition` | the sentence the prompt carries verbatim as `key = definition`. The model's idea of "personal" is the instance's idea. |
| `default` | at most one category. Unclassified posts fall into it. |
| `close_friends_only` | platforms on which the label is allowed only when the author is a close friend. Enforced after every verdict and by `refilter`. |

The label answers "what is this post under the owner's definitions". The model may use author identity, close-friend status and author-specific written policy. Posting frequency belongs to views, not category definitions. Personal category names and policies are instance data, never built into the implementation.

### Views

A view answers "do I want to see this now". It may use context the label must not carry, such as how often the author posts. A view has exactly one of five shapes. `category` and `authors` may appear together to require both conditions; no other combination is allowed:

| Shape | Fields | Meaning |
|---|---|---|
| category | `category` | posts the model judged into that category |
| authors | `authors` (list of refs or names) | a deterministic author list. Admits unjudged posts, including through a union. |
| category and authors | `category` plus `authors` | only posts by the listed people with a current-content verdict containing that category. Both conditions are required, even with the model disabled. An empty author list matches nothing. Grants no filter bypass. |
| rare | `rare: {max_posts, window_days}` | a friend who rarely posts. Defaults 3 and 90. Chapter 10. |
| union | `union` (list of view keys) | the OR of other views, in that order. One level only. |

The view key `all` is built in and always last. An instance may not define it.

### Validation

Each of these is a load error:

- a key that is not a slug;
- a duplicate category key or view key;
- more than one default category;
- an unknown platform in `close_friends_only`;
- the view key `all`;
- a view with no shape or a combination other than `category` plus `authors`;
- a category view that names an unknown category;
- a rare value under 1;
- a union member that is unknown or is itself a union.

Empty labels and definitions are not validated.

## 4.4 preferences.md

A Markdown document. Its structure is part of this specification. Its content is instance data. The line scanner in `Preferences.Parse` accepts the following syntax:

- HTML comments are skipped, over several lines too.
- A section starts at a line that begins with `## `. Deeper headings do not change the section.
- A section is recognized by the lowercased prefix of its heading. Any other section is prose for the human and is ignored.
- Only lines that start with `-` are read in a recognized section.
- A missing file gives an empty preferences object.

| Heading prefix | Line format | Used by |
|---|---|---|
| `never show` and the heading contains `my rules` | `- keyword: <text>`; any other bullet is a load error | the keyword filter (chapter 8) |
| `muted people` | `- mute: <name or ref>`; any other bullet is a load error | the mute filter (chapter 8) |
| `always show` | `- show: <name or ref>`; prose bullets are allowed and ignored | the filter engine, the scorer and the prompt (chapters 8, 9) |
| `plain-english policy` (or `plain english policy`) | `- <sentence>`, taken verbatim | the scoring prompt (chapter 9) |
| `learned from thumbs` | `- <date>: <sentence>. (thumbs #a, #b)`, appended by the curation command | the scoring prompt (chapter 9) |

A ref is `fb:<handle or id>` or `ig:<handle>`. Always-show entries resolve against known authors, including the current observation; which gates they bypass comes from `filters.always_show_bypasses`. A mute name is matched against the display name of the post's author, friend or not, and a mute ref is compared with the author's keys. An entry with a ref prefix of another platform is skipped on this platform. Unmatched and multiply matched entries appear in the rules report so the owner can pin exact refs.

Example:

```markdown
# My feed preferences

## Always show
Some prose the human wrote.
- show: ig:quiet.friend

## Never show — my rules
- keyword: giveaway
- keyword: sponsored by

## Muted people
- mute: fb:loud.page
- mute: Firstname Lastname

## Plain-English policy
- No politics, no elections, no protests.
- Hide posts by pages; I want people.
- A tip or an essay from a friend is interesting, not personal.

## Learned from thumbs feedback
<!-- appended by `Feed.Cli curate`, one dated line per learned rule -->
- 2026-09-24: Posts by Some Person are acquaintance news, not personal life. (thumbs #12, #15)
```

The policy hash (chapter 9) covers the policy lines, the show lines and the learned lines. It does not cover keywords, mutes or prose, so a keyword edit does not invalidate stored verdicts.

## 4.5 Reload

The web host checks for changes before each request and each scheduler tick, including while scheduling is disabled. When any of the three files changes, load and validate the complete set, then atomically publish one immutable snapshot. Each request or tick uses one snapshot throughout; concurrent readers never see a partially published reload. Changes therefore take effect without a page visit or restart. A load error keeps the last good set and shows on the debug page. At start there is no last good set, so a malformed file stops the host with the error. The web host, port and log level are read once at start. A CLI process reads the files once at start.

**Current implementation.** `InstanceFiles` reads the three texts and hashes their combined content, reusing the parsed snapshot when unchanged. The contract does not require this particular change-detection mechanism. Change detection must catch replacements/edits and reconcile missed notifications, then publish only a fully validated snapshot. A long-running CLI keeps its startup snapshot; chapter 12 documents cancellation and restart so applying an edit never requires waiting for an entire processing backlog.

## 4.6 Environment variables

| Variable | Meaning |
|---|---|
| `FEED_DATA` | the instance directory |
| `FEED_CLI` | the CLI executable the web host starts; overrides the lookup next to itself |
| `FEED_STAGE_DIR` | the profile staging root |
| `FEED_NO_SANDBOX` | `1` runs Chromium without its sandbox |
| `ASPNETCORE_URLS` or `--urls` | overrides `web.host` and `web.port` |
| `DISPLAY` | when set, the browser uses that display and no virtual display is started |
| `LANG` or `LC_ALL` | the browser locale, `xx_YY.UTF-8` becomes `xx-YY` |
| `TZ` | the browser timezone fallback, only in the `Area/City` form |

## 4.7 Agent-assisted instance setup

The owner may describe the desired feed to an operating agent. The agent translates that intent into the three instance files; the same setup is possible by hand. No agent service, MCP server or model-driven setup UI is required in the application.

- Deterministic choices belong in `filters`, keyword/mute/show entries, and platform settings. Natural-language judgment and author-specific guidance belong in policy lines. Category definitions and selection views belong in taxonomy. Schedules, model capacity and retention belong in config.
- The agent resolves people to platform refs where possible, reports ambiguous names, and explains rule precedence. It makes clear whether “always show” means a dedicated view, bypassing model hides, or bypassing other explicit gates. It does not infer a mute from thumbs.
- Before enabling a new instance, validate all files and use `rules` to show the effective gates, bypasses, resolved people, categories and views. The owner reviews the translated behavior in ordinary language. The agent must not silently widen an ambiguous exclusion or override; it asks about the unresolved choice.
- The application validates structure and exposes the effective rules. It does not pretend to prove that arbitrary policy prose is logically consistent. Deterministic gates and the model's policy are shown separately so an early exclusion cannot be mistaken for a model decision.
- Setup writes only inside the selected instance. No real name, handle, policy sentence, endpoint or private category is copied into source, fixtures, this specification or a generated public example. Repository examples remain synthetic. Each instance has its own choices; supporting those choices does not add multi-user accounts to one running instance.
