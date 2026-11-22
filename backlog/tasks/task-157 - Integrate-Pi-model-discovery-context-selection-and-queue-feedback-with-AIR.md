---
id: TASK-157
title: Integrate Pi model discovery, context selection and queue feedback with AIR
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T02:15:00Z'
updated_date: '2026-11-22T02:48:00Z'
labels: []
dependencies: []
priority: high
ordinal: 152000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One extension keeps the built-in Pi model picker, session context and queue state aligned with the running AIR catalog.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Catalog refresh and reload preserve the session and selections, including cold entries and removed choices.
- [x] #2 The context command changes profile and budget per model, and resume restores the choice.
- [x] #3 Original Pi requests serve at 32k and 256k with measured residency. Concurrent status, cancellation and errors attach only to the corresponding ticket.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Register the AIR provider, reconcile changes, persist per-model profile choices and validate available transcript capacity. Use the original transport and ticket for status, errors and cancellation. Verify session continuity, real backend profile transitions, and concurrent requests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Installed Pi selected a cold catalog entry after reload, retained its session and streamed DeepSeek at 32768 and 262144 tokens, each independently observed at one slot. A second client cancelled its queued ticket while a third kept its ticket and completed. A follow-up prompt on the final deployment streamed successfully. [Merged integration](https://github.com/ArborealML/Inference-Router/pull/77).
<!-- SECTION:NOTES:END -->
