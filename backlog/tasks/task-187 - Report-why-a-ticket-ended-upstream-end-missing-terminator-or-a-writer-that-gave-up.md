---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-25T23:44:00Z'
labels: []
dependencies:
  - TASK-189
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Slice two on the same branch after slice one is reviewed. Record on each finished ticket how its upstream ended (complete with the completion marker, clean end without the marker, upstream fault) and what the router wrote towards the client (payload write attempts and completed bytes, not token counts), plus the transition or engine epoch in effect. Add the named reasons as additive fields and feed events, reviewed before shipping; keep the existing error-then-completion byte shape untouched. Expose event ring gaps rather than pretending completeness. Pins: an upstream that ends without its marker produces a diagnostic that reads as incomplete while the bytes on the wire stay unchanged; an upstream fault still ends in band with the error frame and completion marker; a cancelled request stays isolated from its siblings.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Wakes when the slice-one branch lands. Plan review for both slices dispatched to the external reasoning service 2026-11-25 (extra-high tier); the plan is in the Implementation Plan section of this task and of the one it depends on.
<!-- SECTION:NOTES:END -->
