---
id: TASK-99
title: >-
  Cap the caller-facing text in keepalive frames and in an error frame on a
  committed stream
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-18T11:14:00Z'
updated_date: '2026-11-20T16:52:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 98000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames will compare the shipped keepalive and committed-stream error frames with the documented wire ceilings and current tests. Identify any remaining caller-facing size gap and the smallest test that distinguishes it. The upstream error-body read ceiling landed separately in [PR #44](https://github.com/ArborealML/Inference-Router/pull/44).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 Keepalive control frames carry the identifier at its reporting length, and a named pin asserts the frame length
- [ ] #2 Each HTTP ceiling is asserted against a byte ceiling named in SPEC, and a long error frame on a committed stream has its own pin
- [ ] #3 A mutation that removes the admission ceiling turns exactly one pin red
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by the deep commit review of 8e6867d; every claim re-measured here before recording: `ReadAsByteArrayAsync` at `RouterCore.cs:708` and `:725`, keepalive interpolation at `ServerApp.cs:244-245`, the ceiling constant at `ErrorContractTests.cs:256,291,322`. The review ran nothing: its five mutation controls are its reading of the pins, not a run, and it reports no CI evidence.
Disposition by the coding seat, recorded here: the keepalive text and the committed-stream error frame land as one follow-up commit right after TASK-94, because both are the mechanism v0.12.40 already owns. The **read side** is refused here and moved to TASK-101 — buffering an upstream error body whole is about what the router *reads*, and wants its own ceiling, pin and commit.
<!-- SECTION:NOTES:END -->
