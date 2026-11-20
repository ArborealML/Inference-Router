---
id: TASK-101
title: >-
  Read an upstream error body under a stated ceiling instead of buffering it
  whole
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T11:29:00Z'
updated_date: '2026-11-20T04:25:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 100000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake: independent plan review of the bounded upstream error-body read. The proposed check counts actual bytes consumed on the error path before the router takes its diagnostic excerpt. Stream Delivery coordinates shared disposal or admission edits with the stalled-reader work.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 The upstream error read stops at a stated ceiling instead of buffering the whole body
- [ ] #2 The false comment at `ErrorContractTests.cs:224` is corrected, and a pin with counted content fails if the read grows past the ceiling
- [ ] #3 A named mutation that removes the read ceiling reddens exactly that pin and no other
- [ ] #4 SPEC states the read ceiling next to the reporting caps, so the numbers sit in one place
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Split out of TASK-100 at the coding seat's request and with agreement: those criteria are about caller-facing text, this one is about what the router reads, and it needs its own ceiling, pin and commit. Verified by this seat at 27f7380: `RouterCore.cs:708` and `:725`, comment at `ErrorContractTests.cs:224`. Sequence after TASK-87; plan review at the deep tier, since it changes a read path and not the wire.
<!-- SECTION:NOTES:END -->
