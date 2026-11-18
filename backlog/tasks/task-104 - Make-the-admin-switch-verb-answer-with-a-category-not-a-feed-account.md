---
id: TASK-104
title: Make the admin switch verb answer with a category, not a feed account
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T12:59:00Z'
labels: []
dependencies: []
ordinal: 103000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The taxonomy work closed every caller-visible refusal on `/v1` and the feed, but `POST /admin/switch` still answers `202` and a feed account: an operator who asks the router to move a model and is refused learns what happened by reading events afterwards, not from the answer in front of them. SPEC §15 records the gap and nothing anywhere carries criteria for it, which is the state the contract calls silence. This is not a naming oversight: the verb has no terminal outcome to name, so the fix is an outcome, not a token.
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
