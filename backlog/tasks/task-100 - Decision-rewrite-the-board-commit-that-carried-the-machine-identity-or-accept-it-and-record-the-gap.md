---
id: TASK-100
title: >-
  Decision: rewrite the board commit that carried the machine identity, or
  accept it and record the gap
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T11:14:00Z'
labels: []
dependencies: []
ordinal: 99000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three seams on caller-facing text and memory, each verified in this tree: an upstream error body is read **whole** before any excerpt is taken (`RouterCore.cs:708`, `:725`, `ReadAsByteArrayAsync`), so a large upstream error makes the process hold the entire response before a cap applies — and `ErrorContractTests.cs:224` states the opposite ("reads at most 256 bytes"); the queued and switching keepalive frames interpolate `ticket.Model` whole (`ServerApp.cs:244-245`), which is a caller-facing frame with no cap; and the byte ceilings in the new HTTP pins are `WireText.ReasonChars * 6 + 256` — an escaping allowance, not a stated wire ceiling — with no pin at all for a long error frame on a stream whose 200 is already committed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 An upstream error body is read under the excerpt budget rather than buffered whole, and the false comment at `ErrorContractTests.cs:224` is gone
- [ ] #2 Keepalive control frames carry the identifier at its reporting length, and a named pin asserts the frame length
- [ ] #3 Each HTTP ceiling is asserted against a byte ceiling named in SPEC, and a long error frame on a committed stream has its own pin
- [ ] #4 A mutation that removes the admission ceiling turns exactly one pin red
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Found by the deep commit review of 8e6867d; every claim re-measured here before recording: `ReadAsByteArrayAsync` at `RouterCore.cs:708` and `:725`, keepalive interpolation at `ServerApp.cs:244-245`, the ceiling constant at `ErrorContractTests.cs:256,291,322`. The review ran nothing: its five mutation controls are its reading of the pins, not a run, and it reports no CI evidence.
<!-- SECTION:NOTES:END -->
