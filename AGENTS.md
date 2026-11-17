# AGENTS.md — working contract

Conventions for humans and coding agents working here. [`SPEC.md`](SPEC.md) is the authority on
behaviour and the wire, and carries the revision log: read the relevant section before changing
behaviour, and update it in the same commit as the change.

**Before you run anything:** a config with lifecycle commands (`select = { exec = … }`) starts and stops
inference backends, so smoke-testing one of those configs acts on the machine — use
`samples/router.safe-smoke.toml` (see Safety below). And run the tool preflight first: `backlog task list`,
`backlog decision list`, and `groma agent-instructions` before any architecture work.

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
(`gh run list --branch <branch> --limit 5`, `gh pr checks <n>`, `gh run watch <id>`) rather than
reproducing it locally. A full local run
belongs at a real boundary — a change that crosses subsystems (concurrency, engine lifecycle, the wire
contract), or a diagnosis, where a failure has to be seen to be intermittent rather than assumed.
CI supersedes an in-flight run when a newer push lands, so read the newest run on the branch, not the
one named after your commit; a `cancelled` run there is not a failure.

`--no-build` judges whatever the last build **in the configuration you named** contained: plain
`dotnet test` means Debug, `dotnet test --no-build -c Release` means Release, and whichever one you pick
is silently stale if that configuration was not rebuilt after your edit. CI is safe only because it builds
Release immediately before testing it. All three shipped samples are loaded by a test as written — `Shipped_Sample_Configs_Load_As_Written`
— while the `config` verb validates one file at a time without binding sockets or running lifecycle
commands.

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
- **The wire contract is not casual.** `/v1/*` stays OpenAI-compatible. Router-generated success and event
  bodies on `/admin/*` are PascalCase and error bodies are one lowercase shape on every surface; the tests
  pin selected fields and that split rather than every field, so treat either casing as frozen — changing
  one is a wire break (SPEC §6, §6.1, and the unresolved decision in §15.1).
- **Quote TOML keys that contain dots**: `[engine.strata.catalog."model.id"]`. Unquoted, the key
  silently nests into junk. After editing any sample, run the sample test or the `config` dry-run on the
  file you changed.
- New dependencies go in `Directory.Packages.props` (central package management).

## Workflow

1. **Review a plan before executing it.** Every plan gets an external pass before the code exists: the
   top-tier reasoning model for plans that are complex (new subsystems, concurrency, lifecycle,
   anything touching the wire contract or a live machine), the deep tier for the rest. A plan review
   attacks the design — missing failure modes, ordering mistakes, a plan that is really two plans,
   anything cheaper that would get the same result — and is given its evidence (the SPEC section, the
   code as it stands) so the reviewer can disagree with specifics rather than generalize.
2. **Commit one feature at a time**: SPEC section, code, tests and a revision-log line in the same
   commit. Before pushing, run the checks that cover your change — build, and the focused tests for the
   area you touched; CI runs the whole suite. Never report a check you did not run as passing.
3. **Every commit gets an external deep-review pass** — the operator's own model-review runs, through the
   same external service as plan reviews — dispatched as soon as the commit lands, covering the
   diff or a bounded range of commits; never the whole repo. Reviews are asynchronous: dispatch, keep
   working, ingest when it lands. A dispatched review is recorded in the task body; it changes neither
   the task's status nor its assignee.

   A review is **evidence, not instruction**. Verify every claim against this code and its tests before
   changing anything, and never paste reviewer prose into a tracked file. Disposition is recorded, not
   implied: fix it in the next commit, or write it up as an accepted gap in SPEC (§15 carries the
   dispositions; §14 is the acceptance criteria a change has to meet) or as a board
   task with acceptance criteria. Silence means nobody decided. Review bookkeeping — request ids,
   tiers, transcripts — stays in `.local/`; the public board and commit messages carry only what the
   review changed. When you ingest a review, name the commit it covered and record each finding's
   disposition where the work lives, so a later reader can tell a decision from an oversight.

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
the full range being pushed: deleting a file later does not un-publish it. One output worth naming:
`air config` prints engine endpoints, key-file locations and lifecycle argv, so never paste its raw
output into a task, review or commit message.

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

## When a human has the next move

Open a task **for that person** — assign them, title it `Review:` or `Decision:` — naming exactly what
they have to decide or approve, what changes under each option, and what it unblocks. Then mark the
dependent task **Blocked** and name the task it waits on. A question asked in chat, or a paragraph
inside another task's body, is not a blocker anybody can act on: if work stops on a person, that
person's task list has to show it.

That task carries the steps, not the topic: the link to the pull request or file, the command to run, the
one line of answer you need back, and roughly how many minutes it costs. A human should be able to close
it without asking a follow-up question.

## Kanban columns

`To Do → In Progress → Waiting → Blocked → Done`, set in `backlog/config.yml`.

- **To Do** — not started. An assignee means claimed, not moving.
- **In Progress** — repo state is being changed for this right now. One at a time.
- **Waiting** — started, and parked on an event that will wake it: a review, a CI run, a pull request
  gate, a predecessor task. The task body names the wake condition.
- **Blocked** — no event will wake it; a person has to decide or grant something (an approval, a scope
  call, runner minutes). This is what keeps the column meaning *a human must move*.

Move the board at each work boundary — any commit or push that changes what the next move is — not at end
of day. Anything sitting `Waiting` for a week without being woken gets re-triaged: dispatched, descoped,
or parked out loud. A column that never changes is a bug in the board, not a fact about the work.
