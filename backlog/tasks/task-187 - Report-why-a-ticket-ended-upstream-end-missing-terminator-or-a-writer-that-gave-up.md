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
- [ ] #6 An additive authoritative protocol outcome distinguishes complete, incomplete, failed and unknown; clean EOF or legacy Completed alone cannot establish successful generation or complete usage.
- [ ] #7 One bounded semantic observation contract supplies first raw byte, first semantic output, model identity, usage availability and authoritative outcome; local Tune uses fixture/local observations, commercial extends it.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One bounded observer in the existing framing path records blank-delimited terminator, finish evidence per choice, structured error, clean EOF, partial tail, idle and cancellation. Precedence: structured error, then transport fault/idle, then terminator, then clean EOF with finish evidence; otherwise incomplete. Cancellation is orthogonal. Preserve PascalCase additive fields, existing event order and §6.1 codes. Pin one terminal event, identical error-then-terminator bytes, and sibling isolation. Legacy state stays compatible; commercial and Tune consumers use authoritative outcome separately. Define shared observation DTO/fixture contract early with the Tune runner; defer writer accounting and provider usage normalization to TASK-210. Missing data stays unavailable; no second framing parser. Drain/retry TASK-190/201, switch explanation TASK-196, limits TASK-30, client budgets/provenance TASK-186/188.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design-pass verdict REVISE; corrections are in the plan. Three implementer scope questions are settled: an in-band upstream end gets no second terminal frame, so bytes stay identical while the ticket settles Failed; a boundary end with no terminator records its end name and changes nothing else; the runtime-versus-probe disagreement criterion moved to the launch-conformance family. Accepted gap: a clean boundary end with no terminator still settles Completed on the state axis while its end name reads otherwise — a wire decision, pinned by the frame-identity test asserting empty terminal bytes with a null finish reason. Naming frozen: complete, upstream_closed_without_terminator, stream_incomplete, failed, unknown; cancellation orthogonal. Two implementer runs timed out and one was stopped with nothing committed; the deliverable is cut to the observer, precedence and the two priority pins, with write accounting deferred. Implementation resumes when the fleet returns to the Flash Next model.
<!-- SECTION:NOTES:END -->
