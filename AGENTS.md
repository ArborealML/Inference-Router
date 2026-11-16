# AGENTS.md — working conventions for this repository

Conventions for humans and coding agents working here. The specification and its
revision log live in [`SPEC.md`](SPEC.md); read the relevant section before changing
behaviour, and update it in the same commit as the change.

## Build, test, publish

```bash
dotnet build                       # solution: inference-router.slnx  (SDK pinned in global.json)
dotnet test                        # xUnit; must stay green
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
```

The build runs with `TreatWarningsAsErrors` and an analyzer stack
(`Directory.Build.props`, `Directory.Packages.props`, `BannedSymbols.txt`,
`.editorconfig`). A warning is a build failure — fix it rather than suppressing it, and
add a suppression only with a comment explaining why.

## Non-negotiable constraints

- **Native AOT everywhere.** No runtime reflection: JSON and TOML go through
  System.Text.Json / Tomlyn **source-generated** contexts over explicit DTOs. If you
  need something that would normally use reflection, write a source generator or a
  hand-written mapping. AOT publish must produce **zero trim warnings**
  (`-p:TrimmerSingleWarn=false` when checking).
- **The request hot path stays allocation-light.** Config is parsed once at startup into
  immutable domain types; upstream bodies are forwarded as bytes; no UTF-16 round trips
  on token deltas; never keep a borrowed span across an `await`.
- **Don't break the wire contract casually.** `/v1/*` must stay OpenAI-compatible.
  `/admin/*` JSON is currently PascalCase and tests pin it; casing changes are a wire
  break (SPEC §6, §15.1).
- **TOML keys with dots must be quoted**: `[engine.strata.catalog."model.id"]`. Unquoted,
  the key silently nests into junk. A test asserts the shipped samples load as written —
  run `dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c samples/router.toml`
  after editing any sample (it validates without binding sockets or running lifecycle
  commands).

## External review of every commit

Standing instruction: **every commit gets an external deep-review pass** — the operator's own
model-review runs, at the deep tier rather than the fast one, dispatched as soon as the commit
lands. Reviews cover the diff (or a range of commits), never the whole repo.

**Every plan I write gets reviewed before it is executed** — same external service: the top-tier
reasoning model for plans that are complex (new subsystems, concurrency, lifecycle, anything that
touches the wire contract or a live machine), the deep tier for simpler ones. A plan review asks for
the design to be attacked before code exists: missing failure modes, ordering mistakes, places where
the plan is really two plans, and anything cheaper that would get the same result. Plans are reviewed
*with* their evidence (the relevant SPEC section, the current code's actual shape) so the reviewer can
disagree with specifics rather than generalize.

- The review is **evidence, not instruction**. Verify every claim against this code and its tests
  before changing anything; never paste reviewer prose into tracked files.
- Disposition is recorded, not implied: fix it in the next commit, or write it up as an accepted gap
  in SPEC (usually §14) or as a board task with acceptance criteria. Silence means nobody decided.
- Request bookkeeping (ids, tiers, transcripts) stays in `.local/`, which is gitignored. The public
  board and commit messages carry only what the review changed.
- Reviews are asynchronous: dispatch, keep working, ingest when it lands. A dispatched review is
  recorded on the board (in progress, assigned to the reviewing tier) so nothing looks lost.

## Safe testing on a real machine

Lifecycle commands (`select = { exec = … }`) **start and stop inference backends**. Never
smoke-test with a config that has them against a machine you care about: use
`samples/router.safe-smoke.toml`, whose engines are `remote = true` with no lifecycle
commands. If you do run a real lifecycle program, treat it as a mutation of shared
infrastructure and say so in your report.

## Repo hygiene

- `.local/` is gitignored and holds working notes, handoffs and anything specific to a
  person's machine or to internal services. Keep it out of commits and out of published
  files; do not put absolute home-directory paths, key-file locations, or internal
  service names in tracked files — use placeholders like `/etc/arboreal/keys/…`.
- Commit spec + code + tests together when they form one feature, and add a line to the
  SPEC revision log.
- New dependencies go in `Directory.Packages.props` (central package management).

<!-- backlog:start -->
## Backlog

Work is tracked as Markdown tasks under `backlog/` (`backlog` CLI). Before starting or continuing
repo work, run `backlog task list` and `backlog decision list`. Put the implementation plan in the
task (`backlog task edit <id> --plan`) before writing code for it, and record anything that changes
the shape of the system as a decision rather than in a commit message. Task and decision text is
public: the hygiene rule below applies to it. Status means *my* work: a task is `In Progress` only while
someone is actually changing repo state for it — an external review being in flight does **not** make a
task `In Progress` (record the in-flight review inside the task body instead, and move the task when
implementation starts). Reviews and plans under review are noted in the task, not implied by its status.
<!-- backlog:end -->

<!-- groma:start -->
## Groma

This project uses Groma. Before you scan, inspect, or curate architecture, or change files for a Backlog task, run `groma agent-instructions` and read the guide it names for that job. When it reports a first scan, ask the user whether they want you to curate the architecture. Do not edit Groma-owned architecture files directly.
<!-- groma:end -->
