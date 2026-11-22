---
id: TASK-173
title: Keep the active Pi user turn on its original model and profile
status: To Do
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-22T09:40:00Z'
labels: []
milestone: m-4
dependencies: []
references:
  - tools/pi-air/index.mjs
priority: high
type: bug
ordinal: 168000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Capture the AIR model and profile at Pi user-turn start. Carry both through tool continuations and retries until completion or cancellation. Resolve the pending selection when Pi actually starts the next user turn.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Changing /model or /context and enqueueing a follow-up issues no inference or backend lifecycle request.
- [ ] #2 With a Hy4 tool call held open, selecting Qwen and releasing the tool sends every continuation and retry to the original Hy4 model and profile.
- [ ] #3 After the active turn completes, the original follow-up submits exactly once with the newly selected model and profile.
- [ ] #4 Cancellation releases the turn selection. Concurrent sessions and subagents retain independent selections across arbitrary catalog entries.
<!-- AC:END -->
