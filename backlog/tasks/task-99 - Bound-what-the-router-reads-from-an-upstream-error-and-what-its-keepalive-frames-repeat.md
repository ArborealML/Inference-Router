---
id: TASK-99
title: >-
  Cap the caller-facing text in keepalive frames and in an error frame on a
  committed stream
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T11:14:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels: []
milestone: m-1
dependencies: []
ordinal: 98000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three seams on caller-facing text and memory, each verified in this tree: an upstream error body is read **whole** before any excerpt is taken (`RouterCore.cs:708`, `:725`, `ReadAsByteArrayAsync`), so a large upstream error makes the process hold the entire response before a cap applies — and `ErrorContractTests.cs:224` states the opposite ("reads at most 256 bytes"); the queued and switching keepalive frames interpolate `ticket.Model` whole (`ServerApp.cs:244-245`), which is a caller-facing frame with no cap; and the byte ceilings in the new HTTP pins are `WireText.ReasonChars * 6 + 256` — an escaping allowance, not a stated wire ceiling — with no pin at all for a long error frame on a stream whose 200 is already committed.
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
