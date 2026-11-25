---
id: TASK-184
title: Send only the required context from Pi; never a serving profile
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:52:00Z'
updated_date: '2026-11-25T07:56:00Z'
labels: []
dependencies:
  - TASK-183
priority: high
ordinal: 176000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The Pi-side AIR extension still resolves a named tier from the router catalog for its /context choice and carries that name on the request path. Concurrency is not the caller's business: the extension must send the model and the context window it needs, and let AIR pick the tier. Remove the preferred-profile header and stop presenting tier names as a choice; keep a context-size choice only. Contract work is in SPEC §6 and §13 and follows TASK-183, which decides the server half.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The extension sets no profile header, named or preferred, on any inference request.
- [ ] #2 The extension sends a positive required-context value for every request it admits.
- [ ] #3 The /context picker offers context windows, not slot counts or tier names.
- [ ] #4 A context choice that no tier on any engine can satisfy is refused with the router's refusal, not a client guess.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Wakes when TASK-183 lands the server-side contract that says a caller cannot name a tier. The extension today sends a required-context header plus a preferred-profile header and reads tier names from the admin catalog.
<!-- SECTION:NOTES:END -->
