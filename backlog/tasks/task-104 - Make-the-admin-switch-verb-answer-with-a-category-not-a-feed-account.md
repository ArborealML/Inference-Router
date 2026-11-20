---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T12:59:00Z'
updated_date: '2026-11-20T21:21:00Z'
labels: []
milestone: m-7
dependencies: []
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames is auditing POST /admin/switch on 6588da3. Trace what the caller receives on immediate refusal, what a 202 response establishes, and how the operator learns the eventual outcome. Map existing tests, SPEC and recorded decisions before proposing any wire change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 `POST /admin/switch` answers a refusal in the one lowercase error shape the rest of the surface uses, with a category from the closed set or a new one SPEC defines
- [ ] #2 A real HTTP pin asserts status, shape and category for a switch the router will not attempt, and for one it attempted and lost
- [ ] #3 SPEC §5.5 and §6.1 say what an operator does with that answer, and §15 stops listing the gap as open
- [ ] #4 The refusal carries the reason as a field the client reads, not a token inside a sentence — a client must not have to parse prose to branch on it
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Opened by the board from the coding seat's finding at 14a01e2: their grep for this surface across the board returned nothing, and SPEC §15 carries the gap with no criteria attached. Their correction, accepted and on the record: TASK-65 is the commit-identity decision and does not cover this — the taxonomy commit cites TASK-77/87/94/103 only. Wire-adjacent, so its plan goes to the top reasoning tier before code. Criterion #4 came from the deep review of 14a01e2: the taxonomy answer today is a sentence with a parenthesised token at the end, which is text a client has to parse.
<!-- SECTION:NOTES:END -->
