---
id: TASK-70
title: Check the author and committer identity of every commit in the push range
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T07:33:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 70000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decision-26 replaced the identity ruling: commits carry per-agent linkable identities rather than the human mailbox, so the guard is a set rather than a name. Deliverable: a CI step over the **range a push carries**, checking every commit's author and committer against the sanctioned identities (the owner's, plus each configured agent identity), failing the build and naming offenders. A tip-only check is worthless here, because the outliers this repository is cleaning up were per-commit choices inside a range.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A push whose range carries a commit authored or committed outside the sanctioned identity set fails the build and names the commits
- [ ] #2 The sanctioned set is read from configuration, so adding an agent identity is a config change rather than a code change
- [ ] #3 The already-published outliers sit below a recorded floor, and the floor says why in one sentence
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Live near-miss at 07:23Z, the reason this row matters: an identity written into the shared git config from a linked worktree would have misattributed every seat's next commit (decision-42). Per-commit identity overrides (decision-29) are what saved it; this row's author-and-committer check across the push range is the mechanical backstop for the case where such a config write does land.
<!-- SECTION:NOTES:END -->
