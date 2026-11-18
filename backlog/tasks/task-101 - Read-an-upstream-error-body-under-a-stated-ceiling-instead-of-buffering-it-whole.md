---
id: TASK-101
title: >-
  Read an upstream error body under a stated ceiling instead of buffering it
  whole
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:29:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 100000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An upstream error body is read **whole** before the router takes its 256-byte excerpt: `ReadAsByteArrayAsync` at `RouterCore.cs:708` (again at `:725` for a different read), so a large error response from an engine makes this process hold the entire body before any budget applies. The test comment at `ErrorContractTests.cs:224` asserts the opposite — that the producer "reads at most 256 bytes" — so the suite documents a property the code does not have.
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
