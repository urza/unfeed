# 10. Views

You are here: the reading algorithm. Chapter 9 labeled the posts. This chapter specifies how the page turns labels, author lists and posting frequency into the views the owner picks, and how it folds an author's burst into one card. Chapter 14 exposes the result over HTTP and chapter 15 draws it.

Source map: [FeedQuery.cs](../src/Feed.Core/Queries/FeedQuery.cs) · [ViewTests](../tests/Feed.Tests/ViewTests.cs) · [RecoveryHardeningTests](../tests/Feed.Tests/RecoveryHardeningTests.cs).

## 10.1 Label versus view

A category answers what the post is under the owner's definitions and written policy; author-specific guidance is legitimate input. A view selects what the owner wants to read now. It can combine categories, explicit people and observed posting frequency. The distinction is classification versus selection, not a ban on author context in the judge. All personal definitions and view memberships live in the instance.

## 10.2 The five view kinds

| Kind | A post is in when |
|---|---|
| category | the model judged it into that category: a verdict for the current content revision has a label array containing the key |
| authors | its author is one of the listed people. Entries with a colon are exact refs, others are names through the people matcher. Deterministic. It admits visible posts without a current verdict. It does not override a hide; an explicit always-show entry supplies configured gate bypasses. A union containing this clause also admits these unjudged posts. |
| category and authors | both the category and author-list predicates above hold. This is an intersection, not a union. It requires a current-content verdict even with the model off, and never overrides a hide. An empty author list matches nothing. It may be a union member. |
| rare | its author is a friend who had at most `max_posts` dated posts (this one included, hidden ones included) in the `window_days` that end at the post's own date. With the model on, the post must have a verdict for its current content revision. |
| union | any member view admits it. Members are evaluated in order. A post shows once, and the card names the first member that admitted it. |

The built-in `all` view: with the model on, every visible post with a current-content verdict; with the model off, every visible post.

## 10.3 Rare voices

A friend who posts once in two years should be seen even when that post is on the edge. A prolific friend should not flood a view with true labels. Both are about the author's frequency, so both live in views.

- Membership uses every relevant dated friend post (id, author, date), hidden posts included. Per author, count posts in the inclusive `window_days` interval ending at this post's own date. The post is in when that count is at most `max_posts`. Equal timestamps contribute to each applicable count consistently; row visitation order must not change membership.
- The window ends at the post, not today. Advancing the clock alone does not change membership, but backfilled or corrected observations can. This is observed posting frequency, not proof of the author's total activity; incomplete collection can make an author appear quiet. Coverage is shown separately and does not silently alter this predicate.
- The badge text: `first post we have seen`; `first post in <span>` where the span is years at 365 days or more, months of 30 days at 60 or more, weeks at 14 or more, else days (minimum 1); `2nd post in 90 days`, `3rd post in 90 days`, `<n>th post in <window> days`.
- A rare voice does not sidestep the judge. With the model on, the clause admits only judged posts: an unjudged post waits in Unsorted, and a post the judge hid is out of the live selection anyway. So the clause admits exactly the quiet friends' posts that passed the policy, whatever their label. With the model off the clause still applies its friend and frequency conditions, but does not require a verdict.

**Current computation.** `FeedQuery` loads stored post facts, builds sorted per-author date arrays and uses binary bounds for the inclusive window. Full-history loading and recomputation on every request are not required. SQL range/window queries, derived indexes or bounded caches may implement the same predicate and badge facts. Invalidate/recompute for backfills, corrections, identity merges, friend changes and view-parameter changes; a render still reads only. Counts, selected ids and explanations must agree for the same read snapshot, including equal dates and boundary dates. A cached result is not an independent source of category or visibility truth.

## 10.4 Resolution

The resolver produces consistent membership, uncapped counts and explanations. A union admits each post once and identifies the first matching member in taxonomy order, with its label and any rare fact. Those are required outcomes. The current implementation uses one memoized in-memory membership function for counts, selected rows and explanations inside a consistent read transaction. SQL joins/subqueries, shared projections or other query plans may produce the same results without maintaining two separate predicate implementations. No pagination feature is implied.

The page query then applies, in order: the feed mode (chapter 5: live, hidden or unsorted), the platform scope (the chosen platform, else the enabled platforms), the view predicate in live mode, the order, the render cap.

## 10.5 Unjudged is never shown as sorted

The owner would rather see less than see posts sorted wrong.

- "Judged" means a non-null label array with VerdictContentRevision equal to ContentRevision. Policy/configuration-stale verdicts still count; content-stale ones do not.
- With the model on: `all`, category views and rare clauses show judged posts only. Unjudged posts wait in the built-in Unsorted mode, which lists visible unjudged posts of the platform scope with a count in the toolbar. An authors-only view shows them regardless; a category-and-authors view still requires the category verdict.
- With the model off: all admits all visible posts; authors-only and rare clauses keep their deterministic predicates without a judgment requirement. Category clauses, including category-and-authors intersections, use any stored current-content labels and may be empty; they never silently become all. Unions preserve their member predicates.
- Hiding stays fail-open: an unjudged post is never hidden for being unjudged.

## 10.6 Stacks

A close friend on a daily challenge posts 22 personal posts in a month, every one labeled correctly. A per-author cap would drop posts; a person line would make the label lie. Instead the page folds.

- Walk the rendered posts newest first. A post stands alone when folding is off (`ui.stack.min_posts` 0), or it has no author, or no date, or its author is in any view with an explicit author list, including a category-and-authors view (the owner explicitly selected those people).
- Otherwise the post joins its author's current burst when the burst's newest post is within `window_days` of it; else it starts a new burst.
- A burst of `min_posts` or more folds into one stack: the newest post is the lead, the rest sit behind it, newest first. A smaller burst leaves its posts in their own places.
- Nothing is dropped. The feed stays chronological at the lead level. A stack is one layout unit.
- The badge on the lead: `<N> posts today` for a one-day window, `<N> posts this week` for seven, else `<N> posts in <D> days`. N includes the lead.
- Folding applies in every feed mode.

## 10.7 The decision trail

Every card can explain itself. The trail is a list of lines in stack order:

1. The audience path under instance config: friend/following, qualifying tagged friend (with the selected tag rule), explicit audience bypass, unrestricted captured audience, or outside configured audience. Include effective type/keyword/model shields when present.
2. The verdict: not judged yet, content changed since judgment, or score/categories/reason for the current content revision. Append judged under an older policy/model configuration when PrefsVersion differs. Debug detail includes input revision, model, serving endpoint and time; card explanations do not pretend an older answer judged the current text.
3. The view clause, when one admitted the post with a text: `in this view as <member label[ · rare fact]>` for a union, `rare voice: <fact>` for a rare view.
4. In hidden mode: `hidden by <owner>: <reason>`.

The "why here" badge on the card shows the view clause alone:

- in a union view, the admitting member's label, plus the rare fact when that member is a rare view;
- in a rare view, the fact alone;
- nothing in a category, author, category-and-authors or `all` view, where the view is the reason.

## 10.8 Exact repeated-content folding

Separate platform posts sometimes contain identical photos, for example repeated profile-picture updates. In live views, fold these behind their newest card when they have the same resolved author and platform, identical caption/shared/memory fields, meaningful tags and structural flags (ignore redundant self-tags), and the same complete ordered set of known image byte hashes within ten minutes of that newest post. Require at least one image, all manifest image slots present with known hashes, and no videos. Similar images, missing bytes, text-only posts, different captions, different authors and later reposts do not qualify.

Label the group `N repeated posts`. Keep every original row, permalink, media, provenance, visibility, model result, feedback and heart target. The group is expandable without JavaScript; counts still describe original posts. Apply after view membership and the render cap, before ordinary burst folding. Exact repeats may fold for explicitly selected authors; their ordinary unrelated posts retain the existing stack protection. Inspection modes do not apply this repeated-content fold.
