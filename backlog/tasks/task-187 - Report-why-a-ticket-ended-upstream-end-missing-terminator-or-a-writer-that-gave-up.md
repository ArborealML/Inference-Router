---
id: TASK-187
title: >-
  Report why a ticket ended: upstream end, missing terminator, or a writer that
  gave up
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-25T10:49:00Z'
updated_date: '2026-11-26T10:26:00Z'
labels: []
dependencies:
  - TASK-189
priority: high
ordinal: 179000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A stream that ends with no finish_reason and no [DONE] sentinel currently ends as a success and the extension splices the next turn's tokens onto the truncated text. Slice 1 is landed and merged: the truncation is reported with the end reason, the bytes are not replayed into the prompt, and the exact truncated prefix is captured and counted once. Slice 2 is now unblocked: the containment work merged at d28d014, so branch from main and add the missing end-reason arm — a normal upstream close with no terminator is named upstream_closed_without_terminator and a stream with no terminator and no terminal event, or a stall past the idle bound, is named stream_incomplete; a terminal finish_reason with no [DONE] stays a normal success. Also cover the capacity-warning case the owner saw, where one warning killed other agents' streams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A finished ticket states how its upstream ended: complete, ended without a terminator, or failed, and the event feed carries it.
- [ ] #2 A downstream stream that stopped early records the reason and does not read as completed.
- [ ] #3 The existing wire bytes do not change: an in-band error frame and terminator still end a failed stream.
- [ ] #4 When the container runtime and the router probe disagree about an engine, the disagreement is reported without restarting anything.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Slice 2 on main after d28d014. One classification, recorded where the ticket is settled: the completed path (RouterCore.cs:1269-1285) and the failed or cancelled path (:1319-1325) each learn one additive field naming how the upstream ended — complete (terminator frame seen), upstream_closed_without_terminator (clean upstream end with a terminal finish_reason but no terminator), stream_incomplete (no terminator and no terminal event, or the idle bound at RouterCore.StreamRelay.cs:139 and :158 fired), failed (an in-band error frame ended it). The relay already holds the evidence: the sentinel scan in ObservePublished, StreamIdleException, and the finish_reason probe; do not add a second parser. Bytes toward the client count as write attempts and completed bytes, never token counts. Wire bytes stay identical: an in-band error frame plus terminator still ends a stream, and the new fact rides the event feed and the admin ticket view, never the completion body. Additive names go through wire review before shipping; the section 6.1 code list is unchanged. Capacity-warning case: prove an engine-side warning that ends one stream leaves sibling tickets alone and is never recorded as a completed request, and that a warning arriving as text is relayed in band rather than becoming a socket close. AC4: when the container runtime reports one engine and the probe measures another, emit one named disagreement event and change nothing — no restart, no quarantine. Tests: a fake upstream ending without its marker; one ending with finish_reason and no marker; a stall past the idle bound; sibling isolation under a cancelled or failed stream; and byte-identity pins on the existing error-then-completion shape.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Slice 1 is merged, so slice 2 branches from main. A design pass on the slice-2 plan was dispatched to the external reasoning service on 2026-11-26 at the top tier, with the plan itself and the settled-ticket and relay code named as its evidence; it supersedes the 2026-11-25 pass over both slices, which is not re-run. Wake: that design pass landing. Nothing is implemented until it does; the two names this task adds are wire-visible and need a wire review before shipping.
<!-- SECTION:NOTES:END -->
