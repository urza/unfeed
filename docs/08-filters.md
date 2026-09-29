# 8. Filters

You are here: the deterministic rules. Chapter 6 records platform facts and stores posts. This chapter applies the owner's instance choices before the model sees a post. The engine stores its decision; feed queries do not reimplement it.

Source map: [Filters and RuleContext](../src/Feed.Core/Domain/Identity.cs) · [Ingest.cs](../src/Feed.Core/Application/Ingest.cs) · [CLI refilter](../src/Feed.Cli/Program.cs).

## 8.1 The stack

| Order | Gate | Hide owner | Owner input |
|---|---|---|---|
| 1 | Post type | `structural` | `filters.blocked_types` matched against parser facts |
| 2 | Audience | `whitelist` | `filters.audience` and `filters.friend_tag_exception` |
| 3 | Keywords | `keyword` | preferences never-show entries |
| 4 | Muted people | `mute` | preferences muted entries |
| 5 | Model policy | `llm` | policy, category definitions and threshold |

The parser never decides that every owner dislikes a reel, suggestion or event. The default blocked types are a configurable starting point. `HiddenBy = structural` means a type gate hid the post; it is not a permanent ban built into the parser.

Always-show entries bypass only the gates named by `filters.always_show_bypasses`. Defaults bypass keywords and model hides. A mute still wins. Thumbs record feedback and never directly change any gate.

## 8.2 Type gates

At insert, compare the sponsored, suggested, event and reel flags with the configured blocked types. A matching unshielded type hides the post with its type as the reason. Multiple matches use the order sponsored, suggested, event, reel for a stable explanation. An empty blocked-types list disables this gate. A type-hidden post is not scored or downloaded as video while hidden. A later rule edit followed by `refilter` can release it; type facts remain stored.

## 8.3 Audience

With `friends_and_followed`, a post passes when the author is in the platform whitelist, or a tag qualifies under `friend_tag_exception`. `none` disables the exception, `with` admits only photo-context tags, and `any` admits any parsed tag whose identity resolves to a whitelisted author. An unresolved tag never proves membership. The decision trail names the qualifying friend and exception.

With `all_captured`, the audience gate passes all captured authors. This changes filtering, not collection scope. A configured audience bypass for an always-show author also passes this gate. Otherwise the reason is `outside configured audience`. A Facebook page is not made a friend merely because it was followed or appeared in a capture. Friends-list removals still require positive completion evidence (chapter 6).

## 8.4 Keyword rules

Each `- keyword: <text>` is a case-insensitive substring test on caption and shared text. The first hit hides with `keyword: <phrase>`, unless the author has the configured keyword bypass.

## 8.5 Muted people

A `- mute: <entry>` with a colon is an exact lowercased author-ref match; a name uses the people matcher. The reason is `muted: <entry>`. A mute wins over always-show. To include that author, remove the mute explicitly. A thumb never creates a mute.

## 8.6 Always show

Resolve `- show: <entry>` through author keys or the people matcher against known authors. Each match receives the bypass set from config. A shield is effective only for those named gates. `llm` bypass means the score and categories are still recorded, but a low score never hides that author. Type and audience bypasses must be explicit. A dedicated author view does not grant a shield.

The `rules` report shows resolved authors, unmatched/ambiguous entries, the bypass set and mute conflicts. The prompt receives the effective model shields, not a promise that model prose can override earlier gates.

## 8.7 The engine

The pure evaluator is shared by ingest, content refresh and `refilter`. Evaluation order and persisted owner are deterministic:

1. A reserved `thumbs` hide is untouched. An `other` hide is also preserved unless the documented unhide action releases it.
2. Resolve configured bypasses, then evaluate type, audience, keyword and mute gates. The first active exclusion wins. It may replace an older `llm` hide: deterministic exclusions take precedence over derived model output.
3. If no deterministic gate excludes the post, clear any previous deterministic hide. Preserve an existing `llm` hide unless an effective model shield releases it; otherwise only a passing fresh verdict or an owner unhide releases it.
4. Persist visibility and its revision together. SetHidden and ClearHidden preserve the HiddenAt rules from chapter 5.

The documented refilter/content-refresh paths reconcile hide ownership; render never does. The operation reports total, hidden, shown, unchanged and per-owner counts. A configuration change alone does not secretly rewrite historical post decisions during a page request.

## 8.8 When it runs

Every new post is evaluated in its insert transaction, including after a failed or checkpointed capture. A content revision refresh also evaluates deterministic rules atomically. No unchecked row is visible between storage and a later stage. A filter failure rolls back the transaction and leaves the raw for retry.

`refilter [--post-id N | --since <date> | --all]` explicitly reapplies current rules to stored posts; default is since the newest collect began. Use `--all` after a global type, audience, keyword, mute or bypass edit. It also reapplies close-friends-only category restrictions. Refilter takes the relevant ingest lock so it cannot race an ingest transaction; in-flight model results are guarded by VisibilityRevision.

## 8.9 Unhide

The page action clears the four visibility columns and increments VisibilityRevision. It is an explicit owner override. Stored verdicts stay; a later explicit rescore, content update or refilter may hide the post again. An answer already in flight against the prior visibility revision cannot overwrite the action.


## Management controls

The People section at `/manage` edits close-friend membership, mute and always-show rules using exact stored platform identities. If removing a person from a name-based rule that currently matches several people, retain the other current matches as exact refs. The Rules section also exposes the complete mute/show lists, including entries with no imported match. Mute is persistent policy, not a change to the platform relationship.

Remove from feed list clears local `IsFriend`, removes matching close-friend and extra home-timeline entries, and skips pending/retry sweep targets. Preserve posts and their action history; reapply audience rules instead of deleting posts. A later add-only import can add the relationship back. These operations never unfriend/unfollow on the platform. A profile already being visited may finish.

After a management edit, reapply deterministic gates, category restrictions and any still-applicable current-content low-score verdict. In particular, unmuting must not inadvertently bypass an existing low model score. Manual hide owners remain protected. The controls do not invoke the model.
