---
id: TASK-120
title: Keep CLI options out of the inference prompt
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T20:16:00Z'
updated_date: '2026-11-19T15:13:00Z'
labels: []
milestone: m-0
dependencies: []
priority: high
ordinal: 118000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent CLI prompt plan review dispatched November 19 wakes the coordinator. Release has prepared the isolated change on 914768d. Both chat and complete currently collect option values as positional text. A server address can bypass stdin, and a system instruction can also enter the user message.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Both air chat and complete send exactly the positional user text with named options. A subprocess test inspects the captured request and verifies system instructions remain in the system message.
- [ ] #2 With options and no positional prompt, both verbs read stdin. Option order preserves the documented request behavior.
- [ ] #3 SPEC section 11 describes the shipped CLI boundary and prompt rules.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Release will preserve first valid option values and aliases, consume repeated recognized option values and the existing inert since option, and retain positional order and trimmed stdin fallback. Reuse the existing CLI subprocess and loopback HTTP fixture for both verbs with positional or stdin input. Verify exact system and user messages, flags and routing headers. Update SPEC section 11. Qualify only CliCompletionProcessTests.
<!-- SECTION:PLAN:END -->
