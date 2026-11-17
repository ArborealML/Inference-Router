---
id: TASK-65
title: 'Decision: which git identity do commits carry?'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T23:14:00Z'
labels:
  - chore
  - governance
dependencies: []
priority: medium
ordinal: 65000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two pushed commits carry an agent-model git identity rather than the repository's configured one: `4dd1e0e` (v0.12.26, SPEC only)
and `20a06fa` (v0.12.27, capability-refusal code), authored as `qwen3.8-flash-next <qwen3.8-flash-next@local>`. Both are on
origin/main in a **public** repository and the address is unresolvable, so GitHub renders both as unlinked authors. The rest of the
history is yours (census in Notes). Nothing in the repository caused it — correct local identity, no global config, no hooks, no
environment override, one worktree — so it was a per-commit override, most likely a literal reading of the rule that an assignee
names the model doing the work. That rule covers the assignee **handle** only, and it should say so. The alternative touches
published history, so the call is a human's.

**Three answers.** **(A)** Leave history, state the rule, gate it with a small `tools/` identity check over the push range, the
shape of `board-check.py`; nothing published changes. **(B)** Rewrite the two authors, force-push main, rename TASK-55 and repoint
every citation; review records keyed to the old shas stop resolving. **(C)** Keep agent attribution deliberately with a real
linkable identity per model, going forward. **Recommendation: A** — the defect is a missing rule, not a corrupted history, and B
spends a force-push on a public repository to settle an ambiguity A removes going forward.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line choosing A, B or C, with the reason if it is not A
- [ ] #2 the chosen rule is recorded as a decision and, for A or C, the working contract says in words that the model-naming rule covers task assignees only
- [ ] #3 if B: both authors rewritten, main force-pushed, TASK-55 renamed and every citation repointed, with the sweep shown
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
No agit-style identity wrapper is installed on this machine, which is why the agent-named commits happened per commit rather than by
configuration; and nothing in the release gate depends on the author identity — the tag and release runs work under the default
author. Carried from the archived duplicate row that first asked the general form of this question.

**Census at 23:1xZ** (`git log --all --format='%an <%ae>' | sort | uniq -c`): 199 default identity, 15 `Claire <claire@localhost>`,
3 `board`, 1 `Ian Diaz <shadowndacorner@gmail.com>`, 2 `qwen3.8-flash-next <qwen3.8-flash-next@local>`.

**Cost of B, measured here:** 7 citations outside this task — TASK-54 twice, TASK-55's title and filename, TASK-56, TASK-57,
TASK-58, and the external review records — plus this task's own text. No tag points at either commit (only `v0.0.9-canary` and
`v0.0.10-canary` exist) and main is unprotected: the sole ruleset is `release-tag-protection` on tags, so a force-push is
mechanically possible.
<!-- SECTION:NOTES:END -->
