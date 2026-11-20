---
id: TASK-101
title: >-
  Read an upstream error body under a stated ceiling instead of buffering it
  whole
status: Done
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T11:29:00Z'
updated_date: '2026-11-20T05:00:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 100000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
For a non-success upstream response, AIR reads through the first 256 application content-stream bytes or EOF. The existing diagnostic and deadline behavior are preserved. The limit applies to application reads; the HTTP transport can separately read ahead or drain.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The upstream error read stops at a stated ceiling instead of buffering the whole body
- [x] #2 The false comment at `ErrorContractTests.cs:224` is corrected, and a pin with counted content fails if the read grows past the ceiling
- [x] #3 A named mutation that removes the read ceiling reddens exactly that pin and no other
- [x] #4 SPEC states the read ceiling next to the reporting caps, so the numbers sit in one place
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Stop reading at 256 bytes or EOF. Document transport read-ahead and draining separately. Count bytes acquired from a nonseekable 5,000-byte body that returns short reads and verify a response timeout before the ceiling. Reproduce the excess read on the baseline with the test alone. Run focused Release tests, remove the ceiling to confirm the counted test fails, restore and verify green, then publish with Native AOT.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The baseline and a deliberate removal of the ceiling consumed 5,000 bytes with the same parsed diagnostic. Only the counted assertion failed under mutation. Restoring the limit passed all 14 focused Release tests and server Native AOT without warnings. [PR #44](https://github.com/ArborealML/Inference-Router/pull/44) landed in v0.12.103 after independent review. [Merge CI](https://github.com/ArborealML/Inference-Router/actions/runs/44677543464) passed on 6c225d2; the landed non-board tree matched the qualified head.
<!-- SECTION:NOTES:END -->
