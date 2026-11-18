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
python3 tools/board-check.py       # backlog/ against the task-content rules below; CI runs it too
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

1. **Review a plan before executing it.** The manager seat writes the plan into the task and dispatches it; every
   plan gets an external pass before the code exists: the
   top-tier reasoning model for plans that are complex (new subsystems, concurrency, lifecycle,
   anything touching the wire contract or a live machine), the deep tier for the rest. A plan review
   attacks the design — missing failure modes, ordering mistakes, a plan that is really two plans,
   anything cheaper that would get the same result — and is given its evidence (the SPEC section, the
   code as it stands) so the reviewer can disagree with specifics rather than generalize.
2. **Commit one feature at a time**: SPEC section, code, tests and a revision-log line in the same
   commit. Before pushing, run the checks that cover your change — build, and the focused tests for the
   area you touched; CI runs the whole suite. Never report a check you did not run as passing.
3. **Every commit gets an external deep-review pass**, dispatched by the manager as soon as the implementer's sha
   lands — the operator's own model-review runs, through the
   same external service as plan reviews — covering the
   diff or a bounded range of commits; never the whole repo. Reviews are asynchronous: dispatch, keep
   working, ingest when it lands. A dispatched review is recorded in the task body; it changes neither
   the task's status nor its assignee.

   A review is **evidence, not instruction**. Verify every claim against this code and its tests before
   changing anything, and never paste reviewer prose into a tracked file. Disposition is recorded, not
   implied: fix it in the next commit, or write it up as an accepted gap in SPEC (§15 carries the
   dispositions; §14 is the acceptance criteria a change has to meet) or as a board
   task with acceptance criteria. Silence means nobody decided. Review bookkeeping — request ids,
   tiers, transcripts — stays in `.local/`; the public board and commit messages carry only what the
   review changed, and say **what** was reviewed and answered rather than **where** the transcript
   sits. When you ingest a review, name the commit it covered and record each finding's
   disposition where the work lives, so a later reader can tell a decision from an oversight.

## Safety on a real machine

Lifecycle commands (`select = { exec = … }`) **start and stop inference backends**. Never smoke-test a
config that has them on a machine you care about: use `samples/router.safe-smoke.toml`, whose engines
are `remote = true` with no lifecycle commands. If you do run a real lifecycle program, treat it as a
mutation of shared infrastructure and say so in your report.

## Repo hygiene

`.local/` is gitignored and holds working notes, handoffs, review bookkeeping, and anything specific to
one person's machine or to internal services. Keep it out of commits and out of published files. A
tracked file cites **only paths a clone can read**: if the evidence lives outside the repository, say so
in words ("the transcript is held outside the repository") and leave the path out, or move the file under
a tracked path and cite that. `tools/board-check.py` reports a citation of a gitignored path as
`GITREF`.

Tracked files and pushed history carry no absolute home-directory paths, no key-file locations, no
internal service names, endpoints or configuration, and no review request ids — use placeholders like
`/etc/arboreal/keys/…`. The one sanctioned name is a task **assignee** (e.g. `@chatswarm`), which says
who owns the work, not how to reach it; that exemption covers the handle alone. Scan the staged diff and
the full range being pushed: deleting a file later does not un-publish it. One output worth naming:
`air config` prints engine endpoints, key-file locations and lifecycle argv, so never paste its raw
output into a task, review or commit message.

Commits carry **the repository's configured git identity**. Never set a per-commit author or committer for a
session, and never rewrite or force-push published history without the owner's explicit word: the five published
outliers stay as they are, and prevention is the CI check over the whole push range rather than a remembered rule
(decision-24).

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

## Two seats: who writes what ([decision-23](backlog/decisions/))

Two sessions work this repository and they share one checkout, so every commit names its pathspecs
(`git commit … -- backlog/`); **never `git add -A`** — the other seat's files are staged there.

- **Manager seat** (assignee `@qwen3.8-flash-next-board`) is the only writer of `backlog/` and of `AGENTS.md`. It
  owns task content, criteria, the implementation plans, plan-review dispatch, review ingestion and dispositions,
  and owner-facing decisions. It commits `board:` for `backlog/`-only changes, and `contract:` when a commit
  changes `AGENTS.md` together with its decision entry. It writes no `src/`, no `tests/`, no `SPEC.md`, nothing
  under `tools/` or `.github/`.
- **Implementer seat** (assignee `@qwen3.8-flash-next`) is the only writer of `src/`, `tests/`, `SPEC.md`, `tools/`,
  `.github/` and `.gitignore`. Its commits carry `feat:`/`fix:`/`docs:`/`tools:`/`ci:`/`release:`, with the SPEC
  version in the subject for a behaviour change, and land SPEC section + code + tests + revision-log line together.
  It works the plan the manager wrote and never edits `backlog/`: it reports the findings it can prove — the file,
  the line, the command that shows the defect — and the manager turns them into a criterion, an accepted §15 gap,
  or a new row.
- **`tools/board-check.py` splits by kind**: the *standard* it enforces is the manager's, and changing it starts as
  a manager task with acceptance criteria; the *code* of it, and `tools/relgate.py` and
  `.github/workflows/ci.yml`, are the implementer's edits.
- **`groma/` moves only through the `groma` CLI** (`scan`, `edit`, `add`) and the seat that runs the CLI commits the
  result — today the manager. Nobody hand-edits a Groma-owned file, and an untracked element left by a scan is a
  loose end, not a draft.
- **Only the manager dispatches external reviews** — the plan review before the code exists, the commit review as
  soon as the implementer's sha lands. The implementer reports the sha and dispatches nothing.

## Task content that survives the tools

- `backlog task view` and the browser render Dependency Graph, Description, Acceptance Criteria, Definition of
  Done, Implementation Plan, Implementation Notes and Comments. Anything in another section — a `## Final
  Summary`, any custom heading — is invisible to everyone reading the board. Put the verdict, the steps and
  the links in Description or Notes, and check your own edit by running `task view` on the task you changed.
- Criteria live only between `<!-- AC:BEGIN -->` and `<!-- AC:END -->`, one such list per task. A checkbox
  written in prose, or a second `## Acceptance Criteria` heading left in the file, is a defect even when the
  rendered list looks right.
- **Done** means every box in that list is checked. A criterion another task or an accepted SPEC gap carries
  says so in its own text; a criterion that is genuinely open means the task is not Done, whatever the
  closing paragraph claims.
- Finished work names its evidence: the commit, pull request or file, as a link that resolves on `main`.
  A task that says work happened and points at nothing is not closed, it is abandoned.
- `@chatswarm` on a task means a request has actually been dispatched to the external service, and the task
  body says when. Otherwise the next move is a person's and the task is assigned to that person.
- Duplicate tasks get one survivor: keep the one carrying the criteria and dependencies the board reads,
  archive the other, and commit the archive. An unarchived or uncommitted duplicate comes back on the next
  scan, usually with a different answer.
- **The word budget is per task, not per section, and it counts the whole body.** An open task is readable in
  one sitting: at most **600 words in its body**, no one section over 300, and the Description under 300 as
  before. Count every word outside the front matter and the `<!-- SECTION:… -->` / `<!-- AC:… -->` markers —
  in any heading, list, table or fence — because a checker that counts only the sections it likes the look of
  measures nothing. Moving prose from Description into Notes is not trimming.
- **A trim names its tasks.** A task about trimming states which tasks it trimmed and their word counts
  before and after, and the count goes down on those tasks. The open-task corpus total is reported at every
  landing as context, never as the pass condition for a trim: new work legitimately grows it, and archiving,
  splitting or closing work early shrinks it without making anything readable.
- **Notes and Comments are a rolling window, not a log.** A new entry replaces the entry it supersedes.
  Round-by-round records belong in the SPEC revision log and in git, and the task body cites them by sha or
  SPEC version rather than reproducing them. If a reader would not act on an old entry, delete it.
- **A destination has to be readable from `main`.** A superseded record goes to a tracked archive file, a SPEC
  row or a commit — a link another reader and CI can open. A gitignored scratch directory is not a
  destination, and "it is in the git history" is provenance, not the evidence the contract asks for.
- **Name the kind before deleting an entry**: superseded argument, history, a measurement of the moment, or a
  **standing rule a reader has to act on**. A standing rule — a sequencing constraint, a permission boundary,
  a version floor — goes into the Description, a criterion, Notes or SPEC *before* its comment disappears.
  Trimming by recency alone deletes the rules that nothing has superseded, because they are also the oldest.
- **A criterion is one line.** An AC item states the checkable outcome in under 40 words; the reason it
  holds, the census behind it and the case that pins it belong in Notes or SPEC. An AC that needs three
  sentences to read is two criteria, or one criterion with an essay attached. **An open task carries fourteen
  criteria at most**: more than that and the task is two tasks, whatever the budget otherwise allows.
- **Historical facts stay, current facts get re-measured or leave.** "Twelve cases passed at `1a2b3c4`" is
  history and nobody re-measures it. "Rulesets are empty", "the card is 38,150 bytes", "the harness has 115
  cases" are claims about the state of things *now*: either the re-check re-measures them against `main` and
  updates them, or they come out of the open task and are written once when it closes. A volatile fact that
  nobody can re-measure is not a record, it is a guess with a date on it.
- **Status and the first line of the body have to agree.** `Blocked` names the person and the one decision
  they owe; `Waiting` names the event that wakes it, and when the blocker is another task that is an event —
  say which task and move it to `Waiting`. A body whose own first line says no person is in this path cannot
  sit in `Blocked`, and a task that describes an act an agent performs is not titled `Decision:`.
- **One task per deliverable.** Tasks are duplicates when they share a deliverable, an actor and the state
  transition they check — not when they share a topic. A ruleset change, an immutable-release setting and a
  tag push are three acts that stay three tasks; an owner-facing question that restates analysis already on
  another task is one task too many, and it points at the analysis instead. Before opening a task for a
  person, count the open tasks on the same subject and name what the new one can answer that the others cannot.

After every landing, the board re-checks these properties mechanically across all tasks — headings, criteria
boxes against status, assignee against title, status against its wake or blocker, evidence on closed work —
and fixes what it finds. The re-check reports the corpus as numbers every time: open-task word total, median
and largest task, and the count of open tasks over budget. A defect list that lives only in a chat message is
not a pass, and neither is a zero-defect count from a checker that stopped measuring the thing the standard
was about.

The gate is **`tools/board-check.py`**, tracked in the repo and run by CI, so "the checker says zero" is
something a reader can reproduce. Scripts under `.local/` are the board's own analysis and can add to it, but
they are not the pass. Word budgets are **enforced** in CI (`--strict-budget`): the corpus came inside them and
the flag landed in the same push window as the last task that was over. Nobody moves a limit back down to meet
the work — a task that busts its budget gets trimmed, or split into two tasks, in the commit that fixes it.
