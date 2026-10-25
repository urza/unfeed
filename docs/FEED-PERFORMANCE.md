# Feed navigation performance

## Plan

- Replace detailed feed diagnostics with exact existence checks and lightweight coverage facts.
- Filter/count in SQLite and load full post records only within the configured render cap; retain exact rare-view history and union explanations.
- Enhance feed/person GET navigation with cancellable HTML replacement, history/focus/scroll handling and ordinary-navigation fallback. Batch masonry measurement and insertion.
- Verify query semantics, diagnostics equivalence, navigation and responsive layout with synthetic tests. Keep instance measurements in ignored data/.

## Progress

- Completed lightweight diagnostics, SQLite membership/counts, bounded full-record loading and request-local identity resolution reuse. Rare-view history and union explanations remain exact.
- Completed feed/person HTML navigation with cancellation, asset-version fallback, focus/scroll/history handling, and observer/gallery cleanup. Masonry now batches measurements and insertion.
- Validation: 374 non-browser tests and 22 browser tests passed; the final navigation fallback refinement and coverage projection were rechecked with all non-browser tests plus both navigation tests (376 passed). Release publish and diff whitespace checks passed.
- Browser coverage includes JavaScript-disabled pages, desktop/phone layouts, raised render caps, delegated actions, rapid navigation, scroll restoration, same-URL refresh and failed-fetch history behavior. Inspected synthetic bright/busy wallpaper screenshots at desktop and phone widths.
- Browser hosts used an explicit content root pointing to built web output, avoiding recursive checkout file-watcher startup overhead. No platform compatibility claim is implied.
- Updated chapters 10, 14, 15 and 17 and regenerated the single-page specification. Instance benchmark results remain private. Existing unrelated working-tree edits were preserved.
