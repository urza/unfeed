# Round 1 (2026-11-10)

Target: `git diff 90805de...HEAD` on branch `02-derived-visibility`. Spec: `docs/plans/02-derived-visibility.md`.

Reviewers: neutral, minimalist, performance (reason: a bulk pass over all posts and a startup restamp), ux (reason: new Manage messages, a field hint and CLI output)

## Step back
Verdict: GO

Why: The plan fixes two real problems. A threshold edit used to need a model rescore of thousands of posts, and the two refilter paths gave different results. The code is small (one rule, one bulk pass, one column, one restamp that runs once), and it was checked against a copy of the live database. The owner-unhide marker is worth its migration, because threshold saves now make a reapply a common event. Without the marker, an unhide would not hold. The `Visibility.Model` split weakens the "one engine" claim a little. It is still correct, because otherwise a verdict could hide a post again after the owner unhid it, and a test guards that case. Remove the restamp code later, as its comment says.

## Findings

### 1. The CLI docs leave out the threshold where they tell the reader to use `--all`: Major, FIX
Reviewers: ux. Where: `docs/08-filters.md` 8.8; `docs/04-instance.md` `threshold` row; `docs/13-cli.md` `refilter` row
Finding: An owner edits `llm.threshold` in the config file and runs a plain `refilter`. Only posts since the newest collect change, so older posts with a score of 4 stay visible. The docs named type, audience, keyword, mute and bypass edits for `--all`, not the threshold.
Resolution: 8.8 and the CLI row now name the threshold edit. The `threshold` row in chapter 4 now says that a Manage save applies it and that a file edit needs `refilter --all`.

### 2. The threshold save had a second lock path that `ResumeRefilter` already covers: Nit, FIX
Reviewers: minimalist. Where: `src/Feed.Web/Management.cs` (`SaveSettings`, `ResumeRefilter`)
Finding: The save added a `Hold` helper, a `locked` flag and a second refilter call. With a busy processing lock it still took both ingest locks and held them through the file save, so an import that started then failed as busy.
Resolution: The save takes no lock for a threshold edit. After the file save it calls `ResumeRefilter`, which now returns the counts or null. `Hold` and `locked` are deleted. The three lock lines for rule saves are back to their original form.

### 3. The people page built the close-friend set once per author: Nit, FIX
Reviewers: performance. Where: `src/Feed.Web/Management.cs` (people rows)
Finding: Each row made a new `RuleContext`, and `CloseFriend` then resolved every close-friend entry again, now for all platforms. The page was already slow in this way; the change added a constant factor.
Resolution: The page builds one `ResolvedRules` and shares it with every row. The always-show column uses the same object.

### 4. The threshold hint said "at once", but a busy lock delays the change: Nit, FIX
Reviewers: ux. Where: `src/Feed.Web/Components/Manage.razor` (threshold field)
Finding: During a processing run the save answers "Existing posts follow the new threshold when the active run ends". The hint promised an immediate effect.
Resolution: The hint now says "A change also applies to stored scores, with no model calls. During an active run it applies when the run ends."

### 5. The plan status line was out of date: Nit, FIX
Reviewers: neutral. Where: `docs/plans/02-derived-visibility.md` line 3
Finding: The header said "Not started" above a "Notes from implement" section.
Resolution: The status now says "Implemented on 2026-11-10" and points to the notes.

### 6. A failed threshold save leaves the pending refilter key set: Nit, DECLINE
Reviewers: neutral. Where: `src/Feed.Web/Management.cs` (`SaveSettings`)
Finding: The key is written before the file save. A rejected value (for example 11) or a version conflict leaves the key set, and the next scheduler tick runs one full pass that nobody asked for. The result stays correct.
Resolution: Declined. The rule saves behave the same way today. The order protects the other case: a crash after the file save and before the key write would leave a new threshold that never reaches old posts.

## Checks after the fixes

Fast suite: 418 passed. Browser suite: 28 passed.
