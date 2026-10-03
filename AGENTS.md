# Working on Feed v3

Feed v3 is implemented. Start with README.md, docs/README.md and docs/RUNBOOK.md. The numbered chapters describe intended behavior and compatibility contracts; linked source files describe the current implementation. Inspect relevant code and tests before changing behavior. Known limitations are labeled, not silently promoted into requirements.

Use .NET 10, SQLite, Playwright for .NET, statically server-rendered Razor Components, plain JavaScript ES modules and plain CSS. No Node/npm frontend toolchain or interactive Blazor.

For a new owner's setup, follow the README/runbook conversation, write instance files, validate rules, and guide the human through noVNC login and bounded live checks. Keep scheduling and like-back off until the owner's choices and relevant checks permit them. Routine operation uses the CLI and Debug, not documentation guesses about live state.

All personal names, handles, policies, categories, endpoints, credentials, profiles, captures, screenshots and operational notes belong in ignored data/ (or an explicitly excluded alternative instance directory). Keep private setup/handoff notes in data/SETUP.md. Public fixtures and examples are synthetic. Never copy live responses into tests without sanitizing and reviewing them.

Resolve routine implementation choices autonomously. Preserve behavioral contracts, document material contradictions or limitations, and update affected specification chapters with code changes. Regenerate docs/spec.html after numbered Markdown changes. Use relevant builds/tests; do not claim synthetic tests prove live platform compatibility.

UI readability rule: every page has rotating photographic backgrounds. Never place text or controls directly on the wallpaper, including counts, helper text, dividers, status messages and footers. Give them a sufficiently opaque contrasting surface; blur or text shadows alone are not protection. Check readability against bright and busy backgrounds on desktop and phones.

For substantial work, keep a concise public plan/progress record containing only reusable engineering information. Keep live counts, account-specific findings, machine paths and service state in data/. Continue authorized implementation beyond planning. Do not publish private state in commits, issue bodies, screenshots or release archives.
