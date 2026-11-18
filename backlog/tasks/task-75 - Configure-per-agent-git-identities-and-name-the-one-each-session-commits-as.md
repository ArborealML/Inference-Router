---
id: TASK-75
title: Configure per-agent git identities and name the one each session commits as
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:52:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-10
dependencies:
  - TASK-70
ordinal: 75000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Answered and recorded: decision-29 settles the naming, so this row is configuration.** The identities are `Arboreal Agent (q38fn · board)` and `Arboreal Agent (q38fn · impl)`, with stable handles under a reserved domain that cannot deliver mail and cannot belong to anyone else, applied **per commit** with `-c user.name` / `-c user.email` because both seats commit in one shared worktree and a single config would collapse them into one author. Both seats have committed under those identities since; the check that enforces them is [TASK-70](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-70%20-%20Check-the-author-and-committer-identity-of-every-commit-in-the-push-range.md), which this row now depends on.

The owner delegated the remaining taste question verbatim — "I don't much care, so do your recommendation" — and the recommendation is taken: **no bot account is minted.** The handles stay unlinked on GitHub, which costs only the hover-card, and a bot account buys nothing until author-linking is actually used for something. If it ever is, that is a two-minute owner act and this row's AC #1 already carries the wiring.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each seat commits as its named identity, seat tag included, from the tooling that starts the session rather than by hand
- [ ] #2 The identity is supplied per commit, so the two seats sharing a worktree cannot overwrite one another's configuration
- [ ] #3 No session sets an author ad hoc: the contract names the rule and the guard fails a build that breaks it
- [ ] #4 The sanctioned set lives in configuration that the push-range guard reads, so adding a seat changes one place
<!-- AC:END -->
