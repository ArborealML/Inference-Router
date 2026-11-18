---
id: TASK-75
title: Configure per-agent git identities and name the one each session commits as
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:52:00Z'
updated_date: '2026-11-18T03:34:00Z'
labels: []
dependencies: []
ordinal: 75000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answered and recorded: decision-29 settles the naming, so this row is configuration.** The identities are `Arboreal Agent (q38fn · board)` and `Arboreal Agent (q38fn · impl)`, with stable handles under a reserved domain that cannot deliver mail and cannot belong to anyone else, applied **per commit** with `-c user.name` / `-c user.email` because both seats commit in one shared worktree and a single config would collapse them into one author. The rule lives in the working contract; the enforcement lives in the push-range guard. One act stays with the owner and blocks nothing: minting a bot account, which would make GitHub link an author instead of showing an unlinked handle.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each seat commits as its named identity, seat tag included, from the tooling that starts the session rather than by hand
- [ ] #2 The identity is supplied per commit, so the two seats sharing a worktree cannot overwrite one another's configuration
- [ ] #3 No session sets an author ad hoc: the contract names the rule and the guard fails a build that breaks it
- [ ] #4 The sanctioned set lives in configuration that the push-range guard reads, so adding a seat changes one place
<!-- AC:END -->
