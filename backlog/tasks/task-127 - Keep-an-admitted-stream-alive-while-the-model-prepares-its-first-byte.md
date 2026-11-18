---
id: TASK-127
title: Keep an admitted stream alive while the model prepares its first byte
status: Done
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-18T22:17:00Z'
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
- [x] #1 After channel creation, hold switch readiness or the first upstream byte longer than two keepalive intervals. Capture timestamped SSE comments during each wait, with one monotonically increasing sequence.
- [x] #2 Release an upstream frame in fragments across a heartbeat deadline. No comment splits a frame, and upstream payload and terminal bytes remain intact.
- [x] #3 An upstream timeout or client cancellation releases the request and leaves no heartbeat writer running after the stream ends.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one monotonic heartbeat deadline across StreamReady and the first nonempty read. Keep exactly one pending channel wait; on timer completion recheck bytes and terminal state first. Follow the actual producer completion order and drain buffered bytes before emitting a terminal frame. Stop heartbeats before forwarding the first nonempty chunk. Exercise empty completion, delayed first byte, fragment boundaries, timeout and cancellation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Pre-implementation review dispatched. The coordinator will verify the failure on the current path and run the integrated regression fixture.

Review of the HTTP relay found the first queued loop can emit a second immediate comment. Carry one next-heartbeat deadline across channel installation. If the first payload or terminal result is ready, write that first, and do not inject a comment between fragments.

The [integrated relay and tests](https://github.com/ArborealML/Inference-Router/commit/7c1eb47) passed 37 focused Release cases and all 486 integrated tests. Server Native AOT published successfully. The controlled stream received sequential comments at 0, 4, 8, 12 and 16 seconds with admission at 10 seconds. A UTF-8 code point split across two deadlines was preserved exactly, and timeout and disconnect released the held read and timer. Implementation and fixture reviews dispatched.
<!-- SECTION:NOTES:END -->
