# 11. Like-back

You are here: the one write. Everything before this chapter reads. This chapter specifies how a heart pressed on the page becomes a like on the original post, through the same browser session, with the same human pacing and the same dead-stop on a checkpoint. Chapter 12 shows how the scheduler starts the run. Chapter 15 shows the heart on the card.

Source map: [LikeSender.cs](../src/Feed.Cli/LikeSender.cs) · [Actions.cs](../src/Feed.Core/Application/Actions.cs) · [BrowserSessionTests](../tests/Feed.Tests/BrowserSessionTests.cs).

## 11.1 Rules

- The like is the agent's only platform write. Never a comment, a message, a post or a follow.
- Opt-in per platform through `platforms.<p>.likeback`. Where it is off, the page offers no heart: a control that cannot land is not offered.
- One attempt per heart, a random human delay before it (`likeback.min_delay_seconds` to `max_delay_seconds`, 2 to 10 s), a dead-stop on any checkpoint.
- The `Likes` row is the single like-state authority. No row means no like. The card reads the newest row of the post.
- The web host never opens a browser. The heart press writes rows. The scheduler starts the sender.

## 11.2 Queueing

A press on the heart (`POST /posts/{id}/like`):

1. Validate: the post exists, the platform supports like-back, the post has a like handle and a permalink. A problem answers 409 with the text, and nothing is written.
2. Write a `Likes` row in state `pending`, unless a pending or sent row already exists. A failed row allows a new press.
3. In the same transaction, ensure a run request of kind `like` for the platform when it has no pending or claimed like request. The unique active-request index prevents duplicates. The scheduler also reconciles pending hearts without an active request, so a press racing a sender's last sweep cannot strand the heart.
4. Redirect back. With script, the chip updates in place and polls (chapter 15).

## 11.3 The sender

The scheduler starts `Feed.Cli like --platform X`. The sender:

1. Take the platform lock. When it is held, defer with `platform busy` and exit 75. For scheduler invocations, claim/run registration occurs before lock acquisition as specified in 12.8; a stale claim performs no queue changes. After acquiring the lock, recover pending rows with an old `AttemptedAt` as failed with `outcome unknown; inspect the original before retrying`, including after a standalone CLI sender crashed. Never repeat an uncertain mutation automatically.
2. **Orphan pass.** On this platform, settle every pending row as `failed` when like-back is off, with "like-back is not enabled for <p> (platforms.<p>.likeback)", and return without opening a browser. Disabling collection alone does not disable like-back; its own flag controls sending.
3. When the platform's re-login flag is set, preserve unattempted hearts, defer the request with `needs re-login`, and exit 75. The scheduler waits for login and resumes it automatically.
4. Validate each pending row, oldest first. An invalid row becomes `failed` with the problem.
5. Open one browser session. Run up to 3 sweeps, 3 seconds apart; each sweep re-reads the pending rows, so hearts pressed during the run are picked up. A `--post-id` run does one sweep.
6. For each row: wait the random delay; navigate to the permalink (30 s); check for a checkpoint; poll every second for up to 10 seconds for the control. Absent: fail with "like button not found". Already in the intended reaction state: `sent`, without a click. Check the done names in 11.4 as well as the actionable names, so an already-liked Instagram post is recognized. Otherwise commit `AttemptedAt` immediately before the platform mutation (11.4), press, then poll every second for up to 6 seconds for the liked state. On failure the message quotes the control's accessible name, because the label is the only diagnostic the page gives.
7. Settle each row `sent` or `failed`, with `SentAt` and the error.
8. On a checkpoint: set the re-login flag and stop browser activity. Fail a heart whose mutation was attempted but not confirmed with `outcome unknown; inspect the original before retrying`. Keep all unattempted hearts pending, defer the request with `needs re-login`, and exit 75. They resume after login; failed or sent hearts are never automatically requeued.

The sender writes a like-kind run with its process identity and persists request registration/completion as in 12.8. It owns the browser only through close, sync-back and successful cookie export; unrelated processing can run concurrently. The exit code is 1 when any heart failed, unless the run is deferred (75); otherwise 0. A deferred exit leaves the request pending, with no `FinishedAt`. The Likes rows remain the authority for each heart, regardless of the request's state.

## 11.4 The accessible-name contract

The sender must identify the requested post's own reaction control, recognize its already-done state and confirm the intended reaction. The table is the reference selector strategy for the observed markup: role `button` and accessible name, case-insensitive. A more robust scoped selector may replace it when fixtures and live verification prove the same target and state transitions. Ambiguity fails visibly; it never authorizes clicking a comment, a counter or a different post.

| | Facebook | Instagram |
|---|---|---|
| the control, any state | `like`, `like post`, `remove like`, `remove love`, `remove care`, `remove haha`, `remove wow`, `remove sad`, `remove angry` | `like`, `like post`, `unlike`, `remove like`, `unlike post` |
| done | `remove love` | `unlike`, `remove like`, `unlike post` |
| which button | the unique post control in a permalink-matched post dialog, excluding comment controls and the background feed | the unique viewport-contained, center-hit-testable match containing a 24 px icon (`svg[width='24']`) on the requested permalink |
| the press | hover the control, wait up to 4 seconds for the picker option named `love`, click it | click the control |

On Facebook the heart is the Love reaction, one level down in the picker that opens on hover. A thumbs-up or any other reaction already on the post counts as likeable and is switched: the owner's heart is the newest word. On Instagram the like is the heart. On a photo post every comment has a 16 px heart with the same name, and they come before the post's own 24 px control in the page, so the size filter is what keeps a stranger's comment safe. Any remaining ambiguity fails without clicking; DOM order alone is not proof of the requested post's control.

Facebook permalink navigation may retain an unrelated feed behind nested post dialogs. The sender verifies that the current URL identifies the requested post and requires a matching post link in the candidate control's nearest dialog. Tracking query parameters and fragments are ignored for `/posts/<id>` links; `story_fbid` identifies posts in `permalink.php` and `story.php` links. Links inside named article containers (comments/replies) or nested dialogs cannot establish ownership of the surrounding dialog. Controls inside named articles and controls containing a 16 px SVG are excluded. More than one remaining control fails even if their reaction states differ; the same scope is used to confirm Love. There is no fallback to a unique background control while the dialog is loading.

Instagram navigation must retain the requested shortcode on an Instagram host; `/p/`, `/reel/` and `/reels/` paths are recognized. Reel pages can pre-render many offscreen reels whose controls Playwright still considers visible. A candidate's entire bounding box must fit inside the viewport and a hit test at its center must reach that control or a descendant. The sender never scrolls to resolve those prefetched hearts. Uniqueness includes both actionable and done states, so a second eligible Unlike control cannot silently confirm a different reel. The same scope confirms the reaction after clicking.

**Observed navigation limitation:** the Instagram reel viewer can redirect a stored reel permalink to a different shortcode. Redirects are followed normally, but the sender rejects that target for both clicking and confirmation. Manual read-only verification can try `/p/<original-shortcode>/`; confirm the final shortcode and the original post's own Unlike control without another click. This alternate URL format has worked in live inspection, but is not a guaranteed platform contract or an automatic sender fallback. The sender still navigates to the stored permalink. See the [runbook](RUNBOOK.md#editing-and-repairing) for the verification procedure. A redirect alone does not establish that liking caused it.

**Current compatibility limit:** Facebook layouts without a provable matching post dialog, other permalink forms, and controls inside named article containers fail without a mutation. Instagram controls outside the viewport, covered at their center, or on unrecognized permalink forms also fail without a mutation. Extending those layouts requires scoped evidence and regression coverage; a page-wide singleton is insufficient proof of the requested post.

The sets are conservative compatibility facts and must be live-verified. A replacement selector retains regression coverage for the 16 px comment heart and the 24 px post control; correctness depends on the target, not permanently on that one DOM attribute. Bringing a platform's like-back live requires one deliberate real heart with `like --post-id N` as part of the checklist, with the platform's like-back already on; with it off the sender fails the heart without a browser.

## 11.5 Commands

`like [--platform X] [--post-id N]`. Without a platform it drains every platform. `--post-id` queues the post when no pending row exists, then drains only that post, so it can re-send over a sent row on purpose.
