---
id: TASK-75
title: Configure per-agent git identities and name the one each session commits as
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T01:52:00Z'
updated_date: '2026-11-18T01:52:00Z'
labels: []
dependencies: []
ordinal: 75000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**@claire owes the identity setup: one sanctioned identity per agent kind, with a resolvable address, and the handle each session must commit as.** Decision-26 moved per-agent identities from later to now, so an agent commit stops being indistinguishable from an owner commit. Creating the identity and choosing its address is an account act, not a repo act; naming the handle is a one-liner. Everything after that is configuration and a contract line, which the two repo seats can do.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line naming the identity an agent session commits as, with its address
- [ ] #2 the identity exists and is resolvable, so an agent commit is attributable rather than anonymous
- [ ] #3 no session sets an author ad hoc: the identity is configured once, in the tooling that starts the session
- [ ] #4 the contract names the rule, and the push-range guard (TASK-70) reads the sanctioned set
<!-- AC:END -->
