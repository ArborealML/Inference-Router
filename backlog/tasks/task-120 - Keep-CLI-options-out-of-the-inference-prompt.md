---
id: TASK-120
title: Keep CLI options out of the inference prompt
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T20:16:00Z'
updated_date: '2026-11-19T15:27:00Z'
labels: []
milestone: m-0
dependencies: []
priority: high
ordinal: 118000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI and the independent commit review dispatched November 19 on [PR #33](https://github.com/ArborealML/Inference-Router/pull/33) wake the coordinator. Exact bf35e24 passed 44/44 Release CLI process cases with zero skips. Four captured outgoing requests verify chat and complete with positional input and stdin, including separation of system and user text. The shared parser fix and SPEC section 11 correction are qualified on the PR branch.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Both air chat and complete send exactly the positional user text with named options. A subprocess test inspects the captured request and verifies system instructions remain in the system message.
- [ ] #2 With options and no positional prompt, both verbs read stdin. Option order preserves the documented request behavior.
- [ ] #3 SPEC section 11 describes the shipped CLI boundary and prompt rules.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Limit the correction to prompt extraction. Exclude all eligible operands of recognized chat options, including duplicates and the inert since option. Preserve first valid lookup, option flags and positional order. Read trimmed stdin only when positional text is absent. Verify both verbs through the real CLI and a loopback capture; cover option interleaving, duplicates, missing operands, default streaming and exact messages. Align SPEC section 11 with the actual HTTP path.
<!-- SECTION:PLAN:END -->
