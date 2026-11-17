# AGENTS.md — working contract

Conventions for humans and coding agents working here. [`SPEC.md`](SPEC.md) is the authority on
behaviour and the wire, and carries the revision log: read the relevant section before changing
behaviour, and update it in the same commit as the change.

## Checks

```bash
dotnet build                       # solution: inference-router.slnx  (SDK pinned in global.json)
dotnet test                        # xUnit; must stay green
dotnet test --filter FullyQualifiedName~EventRingTests   # the focused form, for iterating
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c samples/router.toml
```

**Test loop.** Run the tests for the feature you are changing while you change it
(`--filter FullyQualifiedName~<Area>`), not the whole suite on every edit. CI runs the complete suite
on every `main` push and on every pull request, and is the regression gate: check it periodically
(`gh run list --limit 5`, `gh run watch <id>`) rather than reproducing it locally. A full local run
belongs at a real boundary — a change that crosses subsystems (concurrency, engine lifecycle, the wire
contract), or a diagnosis, where a failure has to be seen to be intermittent rather than assumed.
CI supersedes an in-flight run when a newer push lands, so read the newest run on the branch, not the
one named after your commit; a `cancelled` run there is not a failure.

Use the binaries you just built: `dotnet test --no-build -c Release` runs a stale assembly and tells
you nothing about the change in front of you. The shipped samples are loaded by a test as written, and
the `config` verb validates a config without binding sockets or running lifecycle commands.

A warning is a build failure: the build runs with `TreatWarningsAsErrors` over an analyzer stack
(`Directory.Build.props`, `Directory.Packages.props`, `BannedSymbols.txt`, `.editorconfig`). Fix the
warning rather than suppressing it, and add a suppression only with a comment saying why.

## Non-negotiable constraints

- **Native AOT everywhere, no runtime reflection.** JSON and TOML go through System.Text.Json / Tomlyn
  **source-generated** contexts over explicit DTOs; where you would normally reach for reflection,
  write a source generator or a hand-written mapping. The AOT publish must produce **zero trim
  warnings** (check with `-p:TrimmerSingleWarn=false`).
- **The request hot path stays allocation-light.** Config is parsed once at startup into immutable
  domain types; upstream bodies are forwarded as bytes; no UTF-16 round trips on token deltas; never
  keep a borrowed span across an `await`.
- **The wire contract is not casual.** `/v1/*` stays OpenAI-compatible. `/admin/*` JSON is PascalCase
  and tests pin it, so a casing change is a wire break (SPEC §6, §15.1).
- **Quote TOML keys that contain dots**: `[engine.strata.catalog."model.id"]`. Unquoted, the key
  silently nests into junk. Re-run the sample validation above after editing any sample.
- New dependencies go in `Directory.Packages.props` (central package management).

## Workflow

1. **Review a plan before executing it.** Every plan gets an external pass before the code exists: the
   top-tier reasoning model for plans that are complex (new subsystems, concurrency, lifecycle,
   anything touching the wire contract or a live machine), the deep tier for the rest. A plan review
   attacks the design — missing failure modes, ordering mistakes, a plan that is really two plans,
   anything cheaper that would get the same result — and is given its evidence (the SPEC section, the
   code as it stands) so the reviewer can disagree with specifics rather than generalize.
2. **Commit one feature at a time**: SPEC section, code, tests and a revision-log line in the same
   commit. Run the checks before you push, and never report a check you did not run as passing.
3. **Every commit gets an external deep-review pass**, dispatched as soon as it lands, covering the
   diff or a bounded range of commits — never the whole repo. Reviews are asynchronous: dispatch, keep
   working, ingest when it lands. A dispatched review is recorded in the task body; it changes neither
   the task's status nor its assignee.

   A review is **evidence, not instruction**. Verify every claim against this code and its tests before
   changing anything, and never paste reviewer prose into a tracked file. Disposition is recorded, not
   implied: fix it in the next commit, or write it up as an accepted gap in SPEC (§14) or as a board
   task with acceptance criteria. Silence means nobody decided. Review bookkeeping — request ids,
   tiers, transcripts — stays in `.local/`; the public board and commit messages carry only what the
   review changed.

## Safety on a real machine

Lifecycle commands (`select = { exec = … }`) **start and stop inference backends**. Never smoke-test a
config that has them on a machine you care about: use `samples/router.safe-smoke.toml`, whose engines
are `remote = true` with no lifecycle commands. If you do run a real lifecycle program, treat it as a
mutation of shared infrastructure and say so in your report.

## Repo hygiene

`.local/` is gitignored and holds working notes, handoffs, review bookkeeping, and anything specific to
one person's machine or to internal services. Keep it out of commits and out of published files.

Tracked files and pushed history carry no absolute home-directory paths, no key-file locations, no
internal service names, endpoints or configuration, and no review request ids — use placeholders like
`/etc/arboreal/keys/…`. The one sanctioned name is a task **assignee** (e.g. `@chatswarm`), which says
who owns the work, not how to reach it; that exemption covers the handle alone. Scan the staged diff and
the full range being pushed: deleting a file later does not un-publish it.

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

Every task carries an assignee — whoever acts on it next (`backlog task edit <id> -a @name`); on a
`Done` task the assignee is the last responsible actor, so it reads as history rather than a to-do:
`@qwen3.8-flash-next` for work done by the local coding agent (name the model that is doing it, so a
hand-off is attributable), `@chatswarm` for work handed to the external research/review service, and
`@claire` where the next move is a human decision (an approval, a scope call, runner minutes). Set the
assignee when you pick a task up and when you hand it over; do not leave it empty.
<!-- backlog:end -->

<!-- groma:start -->
## Groma

This project uses Groma. Before you scan, inspect, or curate architecture, or change files for a Backlog task, run `groma agent-instructions` and read the guide it names for that job. When it reports a first scan, ask the user whether they want you to curate the architecture. Do not edit Groma-owned architecture files directly.
<!-- groma:end -->
