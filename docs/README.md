# Feed v3 specification

This is the behavior and compatibility specification for the implemented Feed v3 application. It describes what the system guarantees, the public configuration/CLI/HTTP interfaces, and how the parts fit together. Each chapter links to the current source; [the code map](../IMPLEMENTATION.md) records internal choices and known limitations. Setup and operation are in [the runbook](RUNBOOK.md). Frontend development, build and deployment require no Node.js or npm toolchain.

Read chapters 1–3 for the purpose and architecture, then use the table below for a particular subsystem. Each chapter opens with context and source references. These are maintenance and operation documents, not instructions to reimplement the application.

| Chapter | File | What it answers |
|---|---|---|
| 1 | [Vision](01-vision.md) | Why does this exist? |
| 2 | [Overview](02-overview.md) | Who is it for, what is in and out, which rules bind everything? |
| 3 | [Architecture](03-architecture.md) | What are the parts and how does data flow between them? |
| 4 | [Instance and configuration](04-instance.md) | What does the owner provide, in which files and keys? |
| 5 | [Data model](05-data-model.md) | What is stored, and what does each column mean? |
| 6 | [Collectors](06-collectors.md) | How does a post get from the platform into the database? |
| 7 | [Media](07-media.md) | Which files are downloaded, and when are they deleted? |
| 8 | [Filters](08-filters.md) | Which posts are hidden without a model, and by whom? |
| 9 | [The language model](09-llm.md) | How is a post scored, labeled and summarized? |
| 10 | [Views](10-views.md) | Which posts does a view show, and why? |
| 11 | [Like-back](11-likeback.md) | How does a heart become a like? |
| 12 | [Scheduler and runs](12-scheduler.md) | When does what run, and what keeps runs apart? |
| 13 | [Command line](13-cli.md) | Which commands exist, and what does each touch? |
| 14 | [Web host](14-web-host.md) | Which routes exist, and what does the page get from each? |
| 15 | [User interface](15-ui.md) | What does the owner see and do? |
| 16 | [Deployment and operations](16-deployment.md) | Where does it run, and how is it started, logged in and contained? |
| 17 | [Testing and verification](17-testing.md) | What must the tests pin, and what must be verified live? |

`spec.html` holds every chapter in one page with collapsible sections, a table of contents and a text search. From this directory, install the documentation-only dependency with `python3 -m pip install -r tools/requirements.txt` (use a virtual environment where required), then rebuild after an edit with `python3 tools/build_spec_html.py`. The builder preserves the existing HTML page shell and regenerates all chapter content and navigation from the numbered Markdown files. It does not include standalone review pages.

Conventions: plain, precise language. Chapter 2.5 distinguishes behavior contracts, current implementation choices and tuning guidance. Unqualified behavior remains required. A bug or implementation limitation does not redefine a guarantee: known gaps are labeled explicitly and source links are navigation, not a substitute for the contract. Defaults and public interfaces change deliberately with documentation and verification. The chapters contain the current requirements rather than a history of earlier decisions.

The numbered chapters remain the source of truth for intended behavior. [AGENTS.md](../AGENTS.md) explains repository work; [the root README](../README.md) introduces the project and its human/AI setup path. Examples are synthetic; private policies and operational evidence belong in ignored `data/`.
