---
id: TASK-49
title: >-
  Answer the throwaway-repository question for the destructive gate tests; spend
  nothing new
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T13:56:00Z'
updated_date: '2026-11-17T17:45:00Z'
labels:
  - release
  - security
dependencies:
  - TASK-44
priority: high
type: task
ordinal: 49000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answered: yes — one disposable public repository, and no new spend.** The option analysis landed at 17:4xZ and this board
**adopted** it, checked against what this repository measures. The money half was settled by the owner on
2026-11-17: `we're not doing paid minutes for this`; every run here is Actions minutes on a public repository, Linux and
Windows legs only (decision-10 keeps macOS off the board, and macOS is the leg this account is billed for).

**The shape.** One unique, non-fork repository under the organisation, name with a nonce, workflow pinned to the approved
commit, both legs and the tag trigger and the publication sequence identical, and the destructive half run there:
duplicate release on an existing tag (the `422` never observed), asset replacement and tag move against a published
immutable release, draft tampering, publish-then-delete, and whether a `creation` rule with `bypass_actors: []` blocks the
account that pushes a release tag. Delete it afterwards, exporting settings, responses, run ids and hashes first.
**Audit every destination** — `-R`, `GH_REPO`, checkout and API calls — so no lab probe can land on this repository.

**What a rehearsal does not buy.** Rulesets and immutable releases are per-repository: the lab proves the workflow and the
API semantics, never that this repository's protections are installed and effective, so the settings facts are still read
back here on TASK-38 and TASK-45. A lab `tag_name` costs nothing; this
repository's canary name is spent deliberately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the option analysis landed and its answer is recorded here as adopted, with what this board checked locally
- [x] #2 the answer names where each destructive test runs and the shape of the disposable repository: unique name, pinned workflow, both legs, every probe destination audited
- [x] #3 the spend line is settled: two legs, Linux and Windows, no paid macOS minutes, and no new paid minutes requested
- [ ] #4 the disposable repository exists under the organisation, workflow pinned to the approved commit, settings read back, destinations audited, before any destructive case runs
- [ ] #5 it is deleted after the battery, with settings, responses, run ids, hashes and run links exported first, and the deletion reported
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Two probes adopted from the analysis that this board had not planned.** (1) A refusal to move a **published**
release's tag cannot tell ruleset enforcement from immutability: prove the ruleset by probing update and deletion on a
live `v*` tag **before** any release is published, and require a causal error plus a readback — a generic 4xx proves
nothing. (2) `target_commitish` is ignored when the tag already exists, so immutability can freeze the wrong source:
require peeled remote tag commit = approved commit = the producing run's `head_sha`, before publishing and again in the
readback. The first belongs to TASK-38's readback.

**Links**
- The gate a rehearsal would copy: [.github/workflows/release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); its acceptance criteria: [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §14.
- Where the acts land: [TASK-42](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-42%20-%20Run-the-canary-through-the-real-gate-knowing-the-version-name-is-burned.md) canary, [TASK-38](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-38%20-%20Act-put-a-v-ruleset-on-the-repository-and-read-it-back.md) ruleset, [TASK-45](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-45%20-%20Enable-GitHub-immutable-releases-before-the-first-tag.md) immutable releases, [TASK-28](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-28%20-%20Cut-the-first-release-tag-and-watch-the-release-run.md) the first tag.
- Spend rule: decision-10. Live state at 17:39Z: 0 rulesets, 0 tags, 0 releases, immutability off, token `admin: true`.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T14:25:00Z

---

Rolled up under the AGENTS.md duplicate rule: it restated analysis that lives on TASK-42 and TASK-38. One owner question survives here — a second public repository — because a new public repository under the organisation is the owner's call; the canary push and the ruleset POST are agent acts under decision-16, each stating its recommendation rather than asking permission.

---

<!-- COMMENTS:END -->
