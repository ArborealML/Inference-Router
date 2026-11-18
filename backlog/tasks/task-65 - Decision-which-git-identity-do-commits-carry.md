---
id: TASK-65
title: 'Decision: which git identity do commits carry?'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T23:14:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - chore
  - governance
milestone: m-10
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
- [x] #1 one line choosing A, B or C, with the reason if it is not A
- [x] #2 the chosen rule is recorded as a decision and, for A or C, the working contract says in words that the model-naming rule covers task assignees only
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Closed on the owner's ruling, recorded as decision-24: **"I do like A, then C later."** Published history is left untouched — no rewrite, no force-push — the rule moves into the working contract, prevention lands as TASK-70, and linkable per-agent identities are accepted as a later act, not part of this one. Measured at the time of closing: five commits on main carry a non-default author name — two under an agent-model name with an unresolvable address, three under a role name on the account's own no-reply mailbox.

Option B's criterion was removed rather than checked: it described the rewrite sweep, and the owner chose A, so the sweep will never run and a criterion for it cannot be met.
<!-- SECTION:NOTES:END -->
