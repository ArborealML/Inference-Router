---
id: TASK-127
title: Keep an admitted stream alive while the model prepares its first byte
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:43:00Z'
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
