---
id: TASK-74
title: >-
  Decision: rewrite the one commit message that published an internal review
  identifier?
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T01:38:00Z'
updated_date: '2026-11-18T01:38:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 74000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**@claire owes one line: rewrite the message and force-push, or leave it.** A chained shell command at this seat produced board commit `37e1e23` with the wrong commit message — an internal review dispatch log instead of the board note — so that published message names a review identifier, which the working contract says pushed text must not do. The committed content is right: only one task row changed, and the board checker is at zero defects.

Option A, leave it: nothing rewrites published history, and a bare identifier is inert to a reader — it names no service and reaches nothing. Option B, amend the message and force-push main: one commit, no task or CI gate cites its sha today, so nothing has to be repointed, but it is exactly the force-push the identity decision told agents never to do without your word.

**Recommendation: A**, with the hygiene check that prevents a recurrence. Whatever you choose, my own rule changes now: one statement per command, never chain a message heredoc with another heredoc.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line choosing A or B, with the reason if it is not A
- [ ] #2 if B: the amended message carries no identifier, and every citation of the sha still resolves
- [ ] #3 if A: the accepted exposure is recorded here, and the prevention rule is stated in the contract
<!-- AC:END -->
