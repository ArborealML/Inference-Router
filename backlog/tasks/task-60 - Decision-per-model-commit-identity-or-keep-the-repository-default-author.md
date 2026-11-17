---
id: TASK-60
title: 'Decision: per-model commit identity, or keep the repository default author'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T22:49:00Z'
labels:
  - chore
dependencies: []
priority: low
ordinal: 60000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Every commit so far carries the repository's default author identity, not the model that wrote it, because no agit-style identity wrapper is installed here. The working contract asks that work be attributable to the model doing it, and task assignees already name models; commit authorship is the half that is not attributed. Two answers, both cheap: install per-model commit identities so future commits carry the model name (an install plus a config decision about what the email domain is), or decide that the default identity stands and the board and SPEC carry attribution. Nothing in the release gate depends on this — tag and release runs work under the default identity — so this is about the audit trail a reader follows from a task to a commit. Pick one line; it unblocks nothing, it only stops the ambiguity.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line of answer: install per-model commit identities, or keep the default author and let the board carry attribution
- [ ] #2 the choice is recorded as a decision with the identity or the reason quoted
<!-- AC:END -->
