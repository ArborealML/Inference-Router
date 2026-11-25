---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T10:49:00Z'
labels: []
dependencies: []
priority: high
ordinal: 179000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A multi-session failure produced connection-closed errors on clients while the router recorded successful completions and no events. The router's contract currently allows a clean upstream end of stream at a block boundary to add no bytes, so a stream that stops without its terminator can be a success on the server and a failure at the client, and nothing records the difference. There is also no record of why a downstream stream was abandoned. Separately, the container runtime and the router's own probe can give opposite liveness answers for the same engine and neither side reports the disagreement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A finished ticket states how its upstream ended: complete, ended without a terminator, or failed, and the event feed carries it.
- [ ] #2 A downstream stream that stopped early records the reason and does not read as completed.
- [ ] #3 The existing wire bytes do not change: an in-band error frame and terminator still end a failed stream.
- [ ] #4 When the container runtime and the router probe disagree about an engine, the disagreement is reported without restarting anything.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Split out of the capacity-warning diagnosis. Do not silently change the end-of-stream contract; report the difference first. The stall and slow-consumer bounds are already tracked elsewhere.
<!-- SECTION:NOTES:END -->
