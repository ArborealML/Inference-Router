# AGENTS.md — working contract

Conventions for humans and coding agents working here. [`SPEC.md`](SPEC.md) is the authority on
behaviour and the wire, and carries the revision log: read the relevant section before changing
behaviour, and update it in the same commit as the change.

Use `samples/router.safe-smoke.toml` for smoke tests. Both the native Docker provider and legacy
selector commands can start or stop real inference backends. Before architecture work, run
`backlog task list`, `backlog decision list`, and `groma agent-instructions`.

## Checks

```bash
dotnet build                       # solution: inference-router.slnx  (SDK pinned in global.json)
dotnet test --filter FullyQualifiedName~EventRingTests   # the focused form, for iterating
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c samples/router.toml
python3 tools/board-check.py       # backlog/ against the task-content rules below; CI runs it too
```

**Local verification.** Implementers run the smallest filtered tests that exercise the changed behavior
and its failure paths. At integration, rerun the affected tests when resolving conflicts changes the
tested code. CI runs the full Release suite on main pushes and pull requests. Run a full suite locally only
when a named acceptance measurement explicitly requires it or a documented failure occurs only in
the complete suite. Record that reason in the task.

**CI triage.** The coordinator checks main and open PRs after integration, at worker handoffs, and at
least every ten minutes during active implementation. Use `gh run list --workflow ci --branch main`,
`gh pr checks` and `gh run view <run> --log-failed`. Inspect completed failures since the previous
check, including failures followed by green runs. For each distinct unresolved failure, create or
update one high-priority task. Assign a named implementer and record the run link, failing SHA, job,
test or step, observed error and the check that will verify the fix. Track superseded cancellations
through their replacement runs. Verify the fix in CI before closing the task.

Check worker prompts and returned reviews before routine board maintenance. Prepare the next
independent task while a plan is under review. Batch routine evidence updates into a landing so
successive bookkeeping pushes give the running regression suite time to finish.

Ordinary builds and focused tests can run concurrently in isolated worktrees. Coordinate an exclusive
window when a measurement specifies the host condition it needs.

**A measurement series that names a host condition holds the machine.** While it runs, no other seat runs
`dotnet` — build, test or publish — because that load is what the criterion is measuring and a poisoned run is
invisible in the count; git, `gh` and the board checker stay fine. Counts taken inside another seat's activity
are void for that criterion, and the series is re-started rather than patched (decision-55).

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
- Prefer C# for new implementation and test tooling. When extending tooling in another language, consider whether a C# change can reuse the existing fixtures and exercise the same production entry point.
- New dependencies go in `Directory.Packages.props` (central package management).

## README feature status

Keep the [README feature list](README.md#features) current for readers deciding what AIR can do.
Add significant user-facing plans under Planned features. When implementation starts, move the entry
to In progress. When the user-visible
behavior lands with its acceptance evidence, describe the supported scope under Features in the
same change. Keep any unfinished portion under In progress or Planned. The implementer includes
the README update with the feature, and the coordinator checks it against main and the
[delivery milestones](backlog/milestones/README.md) at integration. Update or remove entries when
a capability is withdrawn or the plan changes.

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
4. **Ask the owner once.** Before anything goes to the owner as a question, read `backlog decision list` and the
   task's own text: an accepted decision is the answer of record, and citing it *is* the reply. Re-asking what a
   decision already settles is a context-management defect rather than a clarification, and it charges the owner
   for the same turn twice. When a seat compacts its own context, what has to survive the compaction is the
   settled decisions, the open owner decisions and the exact numbers still in play; everything else may go.

## Safety on a real machine

Use the native provider (`lifecycle = "docker"`) for managed model switches. The external selection
script documents the expected behavior. Existing `select = { exec = … }` configurations invoke the
legacy provider. Either provider can change shared infrastructure.

For routine smoke tests, use `samples/router.safe-smoke.toml`. Its engines are remote and expose
no lifecycle operations. Use the `config` verb to validate a managed configuration offline.
Exercise lifecycle behavior against fakes while live mutation is parked. Record any authorized
live switch and its observed effects in the handoff.

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

**Git identity follows the model and seat.** Use the model that authored the change and its role to
form `Arboreal Agent (<model-id> · <seat>)` and `agent-<model-id>-<seat>@arboreal.invalid`.

| Model | Model ID | Seat |
| --- | --- | --- |
| GPT-6.2 Astra | `gpt62a` | `board` for the coordinator |
| GPT-6.2 Sol | `gpt62s` | `impl` for an implementer |
| Qwen 3.8 Flash Next | `q38fn` | `board` or `impl`, according to its assignment |

Set the identity per commit because linked worktrees share Git configuration. For example, the
coordinator uses `git -c 'user.name=Arboreal Agent (gpt62a · board)' -c user.email=agent-gpt62a-board@arboreal.invalid commit …`.
A Sol worker uses `Arboreal Agent (gpt62s · impl)` and `agent-gpt62s-impl@arboreal.invalid`.
When integrating a worker's commit, preserve its author and use the coordinator's identity for the
committer. Check both with `git show -s --format='%an <%ae> | %cn <%ce>' HEAD` before pushing.
Never rewrite or force-push published history without the owner's explicit word: the published outliers stay as they
are, below a recorded floor, and prevention is the CI check over the whole push range rather than a remembered rule
(decision-24, decision-26 and decision-29).

Commit **messages** obey the same hygiene rule as commit content: no internal identifiers, no endpoints, no key-file
paths. A shell message must be one statement per command — never chain a `-F -` message heredoc with another heredoc
or command in the same call, because a misread delimiter publishes the wrong body under a real commit. One such
message is already on `main` (`37e1e23`): the owner's ruling was to leave it, so it is an accepted exposure and a
lesson, not something to rewrite.

<!-- backlog:start -->
## Backlog

Work is tracked as Markdown tasks under `backlog/` (`backlog` CLI). Before starting or continuing
repo work, run `backlog task list` and `backlog decision list`. Put the implementation plan in the
task (`backlog task edit <id> --plan`) before writing code for it, and record anything that changes
the shape of the system as a decision rather than in a commit message. Task and decision text is
public: the hygiene rule below applies to it. Status means *my* work: a task is `In Progress` only while
someone is implementing the deliverable — an external review being in flight does **not** make a
task `In Progress` (record the in-flight review inside the task body instead, and move the task when
implementation starts). Reviews and plans under review are noted in the task, not implied by its status.

An assigned agent owns its task from preparation through testing and delivery to its coordinator. At each
transition the agent reports the new status and supporting evidence to the manager, who updates the shared
board. The agent follows up on failures and outstanding checks. At handoff it sends the coordinator the
diff or commit, the acceptance criteria covered, check results, and any remaining limitations. The manager
moves the task to Ready for Review and assigns the coordinator. If the coordinator requests changes, the
implementer takes ownership again and repeats the affected checks.

Every task carries an assignee — whoever acts on it next (`backlog task edit <id> -a @name`); on a
`Done` task the assignee is the last responsible actor, so it reads as history rather than a to-do:
use the acting session’s handle from the coordinator and implementer assignments below. Use
`@chatswarm` for work handed to the external research/review service, and
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

`To Do → Blocked → Waiting → Preparing → In Progress → Testing → Ready for Review → Done`, set in `backlog/config.yml`.

- **To Do** — not started. An assignee means claimed, not moving.
- **Blocked** — no event will wake it; a person has to decide or grant something (an approval, a scope
  call, runner minutes). This is what keeps the column meaning *a human must move*.
- **Waiting** — started, and parked on an event that will wake it: a review, a CI run, a pull request
  gate, a predecessor task. The task body names the wake condition.
- **Preparing** — the assignee is investigating, gathering evidence, or writing the implementation plan. Name the work underway in the task. Several tasks can be prepared concurrently.
- **In Progress** — the assignee is implementing the change in its worktree. The concurrency limits below apply.
- **Testing** — the assignee is running or diagnosing the checks for its change. Move back to In Progress when a fix begins.
- **Ready for Review** — the agent has delivered its evidence and the coordinator owns the next review action.

Move the board at each work boundary — any commit or push that changes what the next move is — not at end
of day. Anything sitting `Waiting` for a week without being woken gets re-triaged: dispatched, descoped,
or parked out loud. A column that never changes is a bug in the board, not a fact about the work.

## Coordinator and implementers

The coordinator owns the shared board, writes plans with acceptance criteria and arranges independent plan
and commit reviews. Use ChatSwarm when available. If that service is unavailable, record the pending review
and continue independent work. The coordinator verifies reviewer findings against the diff and tests,
integrates accepted work using the appropriate seat identity, checks CI and records the disposition.

Run each implementer as a long-lived GPT-6.2 Sol agent with high reasoning in its own named tmux session.
The coordinator gives it one task and an isolated Git worktree with a single writer. The implementer owns
preparation, implementation, testing, fixes and the evidence handoff described above. It reports each
transition so the coordinator can update the shared board. After a handoff, send revisions or the next
assignment to the same implementer.

Run four persistent tmux implementers under decision-63, each with one task and its own worktree.
Use stable session-qualified board handles for active assignments: `@gpt-6.2-sol-config`,
`@gpt-6.2-sol-shutdown`, `@gpt-6.2-sol-release` and `@gpt-6.2-sol-lifecycle`. Transfer a task after
the previous writer hands it off and stops editing. The temporary subagent overlap has ended.

Ordinary builds and focused tests can run concurrently outside a host reservation. Under decision-55
the coordinator announces the measured host and parks competing work before the first run. The series
owner records load per run and releases the host after the whole series. A competing run invalidates
the affected series and requires a fresh start.
When a task is Done, confirm its changes have landed, retain any required private evidence, inspect tracked
and untracked files, and release its worktree with Git. Preserve outstanding changes or evidence before
removal, and move the implementer out of the retired directory before assigning its next worktree.

The coordinator integrates work into the shared checkout. Every commit names its pathspecs
(`git commit … -- backlog/`); **never `git add -A`** — the other seat's files are staged there. **No `git stash`**
in this checkout either: a `stash -u` sweeps the other seat's uncommitted files out from under it, so work in
progress parks under `.local/wip` and a clean tree is measured in another worktree. Identity flags go in the
command (`git -c user.name=… -c user.email=… commit …`), never in shared config; a commit that lands with the
machine identity is reported and tracked, never repaired by rewriting pushed history
([decision-47](backlog/decisions/)).

- **Coordinator** (currently `@gpt-6.2-astra`) is the only writer of `backlog/` and of `AGENTS.md`. It
  owns task content, criteria, the implementation plans, plan-review dispatch, review ingestion and dispositions,
  and owner-facing decisions. It commits `board:` for `backlog/`-only changes, and `contract:` when a commit
  changes `AGENTS.md` together with its decision entry. It writes no `src/`, no `tests/`, no `SPEC.md`, nothing
  under `tools/` or `.github/`.
- **The assigned implementer** is the single writer in its worktree for `src/`, `tests/`, `SPEC.md`, `tools/`,
  `.github/` and `.gitignore`. Its commits carry `feat:`/`fix:`/`docs:`/`tools:`/`ci:`/`release:`, with the SPEC
  version in the subject for a behaviour change, and land SPEC section + code + tests + revision-log line together.
  It works the plan the manager wrote and never edits `backlog/`: it reports the findings it can prove — the file,
  the line, the command that shows the defect — and the manager turns them into a criterion, an accepted §15 gap,
  or a new row.
- **`tools/board-check.py` splits by kind**: the *standard* it enforces is the manager's, and changing it starts as
  a manager task with acceptance criteria; the *code* of it, and `tools/relgate.py` and
  `.github/workflows/ci.yml`, are the implementer's edits.
- **`groma/` moves only through the `groma` CLI** (`scan`, `edit`, `add`) and the seat that runs the CLI commits the
  result — today the manager, under the `map:` prefix for a commit that touches only `groma/`. Nobody hand-edits a
  Groma-owned file, and an untracked element a scan leaves behind is a loose end, not a draft: describe it through
  `groma edit` and commit it, or remove it, in the turn that finds it.
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
- **A readback names a measurement, not a proxy.** An external, browser-only setting is verified by what a fetch
  from outside its form returns — bytes, byte size, sha256, an exact value — never by a host name inside a URL,
  a thumbnail in a settings pane, or a Save button the product does not have (decision-38). A criterion nobody
  can run is a guess with a checkbox on it.
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
something a reader can reproduce. A change to **what the gate reports** — a new defect class, or a widened or
narrowed one — needs a decision entry naming the standard it enforces: the record sits under
`backlog/decisions/`, and the `tools:` commit that changes the gate cites it by id (decision-54). Scripts
under `.local/` are the board's own analysis and can add to it, but they are not the pass. Word budgets are **enforced** in CI (`--strict-budget`): the corpus came inside them and
the flag landed in the same push window as the last task that was over. Nobody moves a limit back down to meet
the work — a task that busts its budget gets trimmed, or split into two tasks, in the commit that fixes it.
