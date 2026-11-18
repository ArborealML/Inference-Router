---
id: decision-23
title: >-
  Seat inversion: the manager writes backlog/ and the plans, the implementer
  writes src, tests, SPEC and tools
date: '2026-11-18T00:25:00Z'
status: accepted
---
## Context

The two sessions that work this repository inverted what they do on the owner's request: the seat that used to
write code now owns tasks, planning and review, and the seat that used to keep the board now implements. That
leaves `AGENTS.md` "One writer for the board" describing the old arrangement backwards, and three ownership
questions unset — which seat writes `backlog/`, which seat may edit `tools/board-check.py` against
`tools/relgate.py` and `.github/workflows/ci.yml`, and who dispatches the external review that AGENTS.md requires
"as soon as the commit lands". Unowned gates are the reason a rule stops being enforced, so this decision names
each one, and `AGENTS.md` is rewritten to match in the same commit.

## Decision

**Manager seat** (assignee `@qwen3.8-flash-next-board`) is the only writer of `backlog/**` and of `AGENTS.md`. It
owns task content, acceptance criteria, the implementation plans, plan-review dispatch, ingestion and disposition
of every external review, the review bookkeeping held outside the repository, and owner-facing decisions. It writes no
`src/**`, no `tests/**`, no `SPEC.md`, no `tools/**`, no `.github/**`.

**Implementer seat** (assignee `@qwen3.8-flash-next`) is the only writer of `src/**`, `tests/**`, `SPEC.md`,
`tools/**`, `.github/**` and `.gitignore`. It works the plan the manager wrote, lands SPEC section + code + tests +
revision-log line in one commit, reports its sha and its findings (file, line, the command that shows them), and
never edits `backlog/**` or `AGENTS.md`. A finding becomes a criterion, an accepted SPEC §15 gap or a new row only
through the manager.

**`tools/board-check.py` is split by kind:** the *standard* it enforces — what counts as a defect, the word
budgets, the status/assignee rules — is the manager's, and a change to it starts as a manager row with acceptance
criteria; the *code* of the tool, and `tools/relgate.py` and `.github/workflows/ci.yml`, are the implementer's
edits. The three CI jobs stay `board contract`, `build + test`, `release gate harness`.

**Review dispatch belongs to the manager alone.** It dispatches the plan review before the code exists, at the tier
AGENTS.md requires, and the commit review as soon as the implementer's sha lands. The implementer dispatches
nothing and requests nothing; it reports the sha and keeps working.

**Commit prefixes.** `board:` for commits touching `backlog/**` only; `contract:` for a commit that changes
`AGENTS.md` and its decision entry together; the implementer's set stays as history has it — `feat:`, `fix:`,
`docs:`, `tools:`, `ci:`, `release:` — with the SPEC version in the subject for a behaviour change. Both seats
commit in the shared worktree with explicit pathspecs (`git commit … -- backlog/`), never `git add -A`.

## Consequences

Both seats can be replaced without a reader guessing who owns a file: one handle writes the board, one writes the
code, and the gate each one is policed by has a named owner. It also puts a serialization point on the board — the
implementer's findings reach the tasks as messages, so a criterion appears only after someone with the whole board
reads it, and the manager is the one who has to keep the word budgets and the status rules green in CI. Review
routing is now testable: if a landed sha has no review against it, that is a manager failure, not an unassigned
one. Nothing here changes what either seat may run — lifecycle configs still act on the machine, and only
`samples/router.safe-smoke.toml` is safe to smoke-test.
