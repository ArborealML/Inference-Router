---
id: TASK-173
title: Keep the active Pi user turn on its original model and profile
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-22T09:40:00Z'
updated_date: '2026-11-22T10:13:00Z'
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
- [ ] #5 A foreground child inheriting the parent provider callback sends its own model and thread ID. Only a matching options.sessionId can use the parent pin. A child loading AIR independently uses its own turn pin.
- [ ] #6 Before follow-up dispatch, threshold compaction respects the active turn capacity. A smaller pending profile cannot trigger summarization on its model. Legitimate automatic summaries retain separate routing identity, and manual compaction keeps its existing behavior.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace the installed Pi user-message dispatch and follow-up dequeue events. Capture the AIR model and serving profile at that boundary within the session and retain them through retries and tool continuations. Keep selection changes local. Exercise the actual extension with a held tool result, queued follow-up, cancellation and concurrent sessions. Check the provider request headers and model bytes for each submission.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The candidate in PR 80 passed eleven Pi fixture tests and the catalog check. Independent review of the two preceding commits identified summary-thread attribution and explicit per-call output bounds. The writer is addressing these plus legitimate active-window compaction; the inherited child and registry-call attribution changes are under review. Cross-provider pinning is outside the configured AIR-model scope.
<!-- SECTION:NOTES:END -->
