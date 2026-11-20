---
id: TASK-101
title: >-
  Read an upstream error body under a stated ceiling instead of buffering it
  whole
status: In Progress
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T11:29:00Z'
updated_date: '2026-11-20T04:32:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 100000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames is implementing a 256-byte read ceiling for non-success upstream HTTP responses. The ceiling counts application content-stream bytes. Preserve the existing UTF-8 diagnostic, caller response and deadline behavior.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 The upstream error read stops at a stated ceiling instead of buffering the whole body
- [ ] #2 The false comment at `ErrorContractTests.cs:224` is corrected, and a pin with counted content fails if the read grows past the ceiling
- [ ] #3 A named mutation that removes the read ceiling reddens exactly that pin and no other
- [ ] #4 SPEC states the read ceiling next to the reporting caps, so the numbers sit in one place
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Stop reading at 256 bytes or EOF. Document transport read-ahead and draining separately. Count bytes acquired from a nonseekable 5,000-byte body that returns short reads and verify a response timeout before the ceiling. Reproduce the excess read on the baseline with the test alone. Run focused Release tests, remove the ceiling to confirm the counted test fails, restore and verify green, then publish with Native AOT.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The revised plan passed independent review against source at 848ad15. The successful streaming path is covered separately by TASK-30.
<!-- SECTION:NOTES:END -->
