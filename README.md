# Feed v3

Feed v3 is a self-hosted, chronological reader for Facebook and Instagram. It uses a real browser logged into your accounts to collect posts, applies your written rules and optional AI classification, and presents the results in one private feed with categories and views you control. It runs on .NET 10, SQLite and Playwright, with server-rendered HTML, plain JavaScript and CSS.

## Why it exists

Read what matters to you, then get on with your day. Your feed should serve your attention: newest first, without engagement ranking, with rules you can inspect and change. Your data and decisions belong to your instance. The app explains why posts are shown or hidden; it does not post, comment or message. Sending a heart back is optional and requires your action.

## Make it yours

Give your AI assistant this repository's URL and this instruction:

> Help me set up Feed v3 for myself. Clone the repository, read its README, AGENTS.md and operator runbook, and inspect my environment. Ask me what I want to see, then write my configuration, categories and policies only in the ignored data/ directory. Walk me through login in the noVNC browser, verify a few small collection and processing runs with me, and help me choose direct or Docker deployment. Keep scheduling and like-back off during setup. Do not copy someone else's rules or ask me to paste passwords into chat.

You need a Linux machine or Docker host that can run Chromium, persistent local storage, and access to your platform accounts. AI sorting uses an OpenAI-compatible chat endpoint; an image-capable local model is recommended. You can also run without classification. A remote model receives the post text and any images sent for classification, so choose the endpoint deliberately.

The feed and noVNC have **no built-in authentication**. Use localhost, an SSH tunnel or an authenticated private proxy; do not expose either directly to the public internet. Browser collection is bounded and stops on login challenges. It cannot guarantee complete history or permanent compatibility with platform changes.

## Instructions for the setup AI

Use [the operator runbook](docs/RUNBOOK.md) for the complete procedure and [the configuration reference](docs/04-instance.md) for accepted keys. Setup configures the existing application; it does not require rebuilding it from a specification.

1. **Inspect and install.** Check the repository instructions, OS, available ports, storage and tools. Choose direct Linux or the supplied Docker Compose deployment with the human. Install the pinned .NET SDK for a direct build and the browser/display/media dependencies in [deployment](docs/16-deployment.md). Use one scheduler per instance.
2. **Understand the desired feed.** Discuss platforms, audience, close friends, mutes, exclusions, always-show behavior, categories, views and summary languages. Ask about ambiguous names and conflicting rules. Then choose a model endpoint/capacity, retention, timezone and eventual schedule. An author view is a selection, not a filter bypass; thumbs are feedback, not automatic mutes.
3. **Create the private instance.** Write `data/config.json` for settings, `data/taxonomy.json` for category definitions and views, and `data/preferences.md` for personal policy. Start with `scheduler.enabled: false`, platform `enabled: false` and `likeback: false`. `init` creates storage, not these policies. Missing files use empty/default settings; test fixtures are synthetic examples, not starter preferences to impose on the human. Keep setup notes, credentials, endpoints, screenshots and diagnostics under ignored `data/` too.
4. **Validate and log in together.** Run `init` and `rules`, explain the effective rules, then open noVNC and run `login` for each requested platform. The human enters credentials and handles two-factor/CAPTCHA in the browser. Dismiss notification prompts and check that the page scrolls. Never loop on a checkpoint.
5. **Verify a small complete path.** Import friends/following, resolve people to exact refs, enable the chosen platforms while keeping scheduling paused, and run a bounded capture and processing pass. Review visible, hidden and unsorted posts with the human. Check media, categories, summaries, coverage and `/debug`; correct rules in `data/`. Repeat a few times, then validate the desired timeline/sweep modes and restart recovery. Record outcomes in `data/SETUP.md`.
6. **Deploy and hand over.** Enable automatic scheduling only when the human wants it and the setup checks pass. Configure direct service supervision or Docker restart behavior, backups and private access. Like-back stays off unless requested; validate it only on a specifically selected post. Explain Debug's indicator, retry states and re-login alerts. Routine scheduling and recovery run without an AI operator, but expired logins and platform changes can still need help.

For a direct installation, after installing the .NET SDK:

```bash
./build.sh
dotnet out/Feed.Cli.dll init
dotnet out/Feed.Cli.dll rules
dotnet out/Feed.Web.dll
```

The default feed address is `http://127.0.0.1:8000`. Install Chromium and display dependencies before login, following the runbook. For Docker, start with [its deployment instructions](docs/RUNBOOK.md#optional-docker); the same instance files and CLI operations apply. `--data <directory>` or `FEED_DATA` selects another instance location; exclude it from Git and Docker build contexts if it is inside a checkout.

## Documentation and development

- [Specification](docs/README.md): behavior guarantees, configuration and source references; [single-page version](docs/spec.html).
- [Operator runbook](docs/RUNBOOK.md): setup, direct/Docker deployment, repair and backups.
- [Code map and design notes](IMPLEMENTATION.md): responsibilities and current limitations.
- [Verification](docs/17-testing.md) and [synthetic measurements](measurements.md): checks and their limits.

```bash
dotnet test tests/Feed.Tests --filter 'Category!=Browser'
dotnet out/Feed.Cli.dll browser install
dotnet test tests/Feed.Tests --filter 'Category=Browser'
```

Browser tests use temporary synthetic instances, not platform accounts. They do not call a real model or send hearts. All personal configuration, captures, profiles and database state belong in `data/`; never add them to a commit or release archive.

## License

YOU CAN USE THIS SOFTWARE "AS IS" (NO WARRANTY) IN ANY WAY YOU WANT, BUT BY DOING SO YOU ACKNOWLEDGE THAT:

Science is a force for human liberation and one of humanity's greatest inventions. Through open inquiry, evidence, and the willingness to correct our errors, we expand our understanding and our ability to improve the human condition.

Technology is the physical manifestation of our discoveries. By building better tools, we overcome limitations, reduce suffering, and create abundance.

Free markets enable cooperation on an extraordinary scale. Through competition, exchange, and entrepreneurship, they reward useful ideas, spread innovation, and help lift people out of poverty.
