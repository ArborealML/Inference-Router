---
id: TASK-127
title: Keep an admitted stream alive while the model prepares its first byte
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-18T21:10:00Z'
labels: []
milestone: m-1
dependencies: []
priority: high
ordinal: 125000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Dispatch installs the stream channel before switching and waiting for the first upstream byte. The relay sends keepalive comments while waiting for the channel, then waits silently on its reader. Preserve the upstream SSE byte and terminal contracts.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 After channel creation, hold switch readiness or the first upstream byte longer than two keepalive intervals. Capture timestamped SSE comments during each wait, with one monotonically increasing sequence.
- [ ] #2 Release an upstream frame in fragments across a heartbeat deadline. No comment splits a frame, and upstream payload and terminal bytes remain intact.
- [ ] #3 An upstream timeout or client cancellation releases the request and leaves no heartbeat writer running after the stream ends.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Keep one writer and one sequence through channel installation until the first upstream bytes or terminal outcome. Race readiness for bytes against the heartbeat interval without consuming or losing a pending read. After upstream bytes begin, preserve verbatim relay. Exercise held activation, empty-channel completion, fragmented frames, timeout and cancellation on the real HTTP path.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Pre-implementation review dispatched. The coordinator will verify the failure on the current path and run the integrated regression fixture.

Review of the HTTP relay found the first queued loop can emit a second immediate comment. Carry one next-heartbeat deadline across channel installation. If the first payload or terminal result is ready, write that first, and do not inject a comment between fragments.
<!-- SECTION:NOTES:END -->
