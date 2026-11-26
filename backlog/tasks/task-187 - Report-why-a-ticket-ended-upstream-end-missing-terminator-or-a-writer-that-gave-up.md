---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-26T11:10:00Z'
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
- [ ] #4 When the container runtime and the router probe disagree about an engine, the disagreement is reported without restarting anything.
- [ ] #5 One ticket produces exactly one terminal event and one terminal state, including the in-band error plus terminator path that today can settle as completed.
- [ ] #6 Cancellation stays its own axis: a client hang-up or an induced abort is never reclassified as an upstream end reason.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Design pass verdict was REVISE and the diagnosis changed the plan. Verified in code: no string finish_reason exists anywhere in src, so a terminal-finish claim is not observable today; the relay reads == 0 to end the pump and calls Complete unconditionally, which emits request.completed and increments the thread counter whether or not a terminator was seen. So add ONE bounded observer wired into the existing framing path before publication, not a second parser afterwards: it records the exact blank-delimited terminator, terminal finish evidence per choice, a structured upstream error frame, a clean read of zero, a partial tail, an idle-bound firing, and local cancellation. Fix precedence now and pin it: structured error, then transport fault or idle, then terminator, then clean EOF with terminal finish evidence, otherwise incomplete EOF; cancellation stays independent and is never reclassified. Count write attempts and locally acknowledged bytes at the writer with a settlement cutoff — never tokens, never remote receipt. Additive reviewed fields only: PascalCase preserved, event order kept, section 6.1 codes unchanged. Pin exactly one terminal event per ticket, byte identity on the error-then-terminator shape, and two-ticket isolation with exact sibling bytes and zero induced lifecycle calls under a cancel, an error and an idle firing. Passive disagreement only: same-generation measurements, deduplicated, with no lifecycle effect, preserving the existing proof withdrawal on contradiction. Defer explicitly: drain and retry policy to 190, switch explanation to 196, default-limit qualification to 30, client budgets and installed-byte identity to 186 and 188.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Both design passes landed and are dispositioned in the plan above. The sibling-death lead they offered — a generic switch path stopping owned peers, pointed at around EngineRuntime.cs:1170-1175 and EngineLifecycleCoordinator.cs:154-161 — I did not verify, so it is a lead for the drain task, not a finding. What I verified myself: no occurrence of finish_reason anywhere in src, the pump ending on a zero read, and Complete emitting request.completed and incrementing the thread counter with no marker evidence. Accepted gaps, recorded rather than fixed: local acknowledgment is not remote consumption; a locally interrupted upstream ending may stay unknown; feed retention carries no restart epoch. Implementation may start on the plan; the feed-visible names are wire-visible and need the usual wire review.
<!-- SECTION:NOTES:END -->
