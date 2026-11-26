---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: To Do
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-26T22:35:00Z'
labels: []
dependencies:
  - TASK-189
priority: high
ordinal: 179000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A stream that ends with no finish_reason and no [DONE] sentinel currently ends as a success, and the extension splices the next turn's tokens onto the truncated text. Slice 1 is merged: the truncation is reported with its end reason, its bytes are not replayed into the prompt, and the truncated prefix is captured once. Slice 2 names how each upstream ended on the finished ticket, and covers the capacity-warning case the owner saw, where one warning killed other agents' streams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A finished ticket states how its upstream ended: complete, ended without a terminator, or failed, and the event feed carries it.
- [ ] #2 A downstream stream that stopped early records the reason and does not read as completed.
- [ ] #3 The existing wire bytes do not change: an in-band error frame and terminator still end a failed stream.
- [ ] #4 One ticket produces exactly one terminal event and one terminal state, including the in-band error plus terminator path that today can settle as completed.
- [ ] #5 Cancellation stays its own axis: a client hang-up or an induced abort is never reclassified as an upstream end reason.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Design pass verdict was REVISE and the diagnosis changed the plan. Verified in code: no string finish_reason exists anywhere in src, so a terminal-finish claim is not observable today; the relay reads == 0 to end the pump and calls Complete unconditionally, which emits request.completed and increments the thread counter whether or not a terminator was seen. So add ONE bounded observer wired into the existing framing path before publication, not a second parser afterwards: it records the exact blank-delimited terminator, terminal finish evidence per choice, a structured upstream error frame, a clean read of zero, a partial tail, an idle-bound firing, and local cancellation. Fix precedence now and pin it: structured error, then transport fault or idle, then terminator, then clean EOF with terminal finish evidence, otherwise incomplete EOF; cancellation stays independent and is never reclassified. Count write attempts and locally acknowledged bytes at the writer with a settlement cutoff — never tokens, never remote receipt. Additive reviewed fields only: PascalCase preserved, event order kept, section 6.1 codes unchanged. Pin exactly one terminal event per ticket, byte identity on the error-then-terminator shape, and two-ticket isolation with exact sibling bytes and zero induced lifecycle calls under a cancel, an error and an idle firing. Passive disagreement only: same-generation measurements, deduplicated, with no lifecycle effect, preserving the existing proof withdrawal on contradiction. Defer explicitly: drain and retry policy to 190, switch explanation to 196, default-limit qualification to 30, client budgets and installed-byte identity to 186 and 188.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design-pass verdict REVISE; corrections are in the plan. Three implementer scope questions are settled: an in-band upstream end gets no second terminal frame, so bytes stay identical while the ticket settles Failed; a boundary end with no terminator records its end name and changes nothing else; the runtime-versus-probe disagreement criterion moved to the launch-conformance family. Accepted gap: a clean boundary end with no terminator still settles Completed on the state axis while its end name reads otherwise — a wire decision, pinned by the frame-identity test asserting empty terminal bytes with a null finish reason. Naming frozen: complete, upstream_closed_without_terminator, stream_incomplete, failed, unknown; cancellation orthogonal. Two implementer runs timed out and one was stopped with nothing committed; the deliverable is cut to the observer, precedence and the two priority pins, with write accounting deferred. Implementation resumes when the fleet returns to the Flash Next model.
<!-- SECTION:NOTES:END -->
