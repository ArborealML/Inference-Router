---
id: TASK-93
title: Bound every feed field at the funnel, and make SPEC true of it
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - wire
  - events
milestone: m-11
dependencies: []
priority: high
ordinal: 92000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The v0.12.37 cap sat on the event Model field and inside the engine emitter, so the ceiling §10 states for one feed reason was untrue of the request path failure producer. That half shipped at 64b9f94. A review of that commit finds the same defect one layer out: §10 promises more than the marker mechanism delivers about identifier identity, and more than the code delivers about a caller error body. Every claim here was re-measured at that commit by reading.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every producer that appends to the feed has its reason text held to the stated budget: a pin posts a 405-character model id through the request path failure producer and reads the stored reason at reporting length.
- [x] #2 SPEC 10 is true of every producer as written, or SPEC narrows the claim to the producers that honour it — either way, in the same commit.
- [x] #3 Engine and profile names reaching a feed field or a caller error body sit at reporting length, matching the number SPEC states.
- [x] #4 A cut cannot split a surrogate pair, and the marker plus dropped-count text fits inside the reserve the cap claims.
- [x] #5 Two ids that become identical once cut stay distinguishable in a feed record — a bounded fingerprint or the ticket id — pinned by a two-id test.
- [x] #6 A terminal error is bounded where the ticket becomes terminal and again at the HTTP writer, pinned by the /v1 status, the one lowercase shape, the message length, and a ceiling in serialized bytes.
- [x] #7 SPEC 10 and 6.1 name the marker a diagnostic distinguisher, collisions and literal-marker aliases possible, and the thread and engine filters excerpt matching; the ticket filter stays exact.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Shipped at [64b9f94](https://github.com/ArborealML/Inference-Router/commit/64b9f94a677faf188b7f71c2e0fd7994ae7731c4): the funnel owns the four fields, the emitter second cut is gone, identifiers are cut before the sentence, and the marker fingerprints, backs off a surrogate pair and names its 32-character reserve; the head is 96, a wire-visible change. Open half, as dispositioned: normalize once where a ticket becomes terminal — Fail (:828-850) and RejectAtAdmission (:923) — before ticket.Error and the exception are set, cutting identifiers before interpolation and capping the whole message at the reason ceiling, which is the defence for ex.Message (:660-663) and the queue-full sentence (:915); bound the error writer too, which forwards ex.Message raw and interpolates a caller string at three sites; on the ticket snapshot keep Model and ThreadId whole and bound only Error; amend §10 to promise bounded text before storage or publication, since a raw provider exception can exist.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Closed at 8e6867d (SPEC v0.12.40), verified by read: one ceiling in `Fail` (`RouterCore.cs:833`) feeding `ticket.Error`, the caller exception and a stream's error frame; `WriteErrorAsync` caps what it serialises; the queue sentence cuts its id before interpolation; `TryGetTicket` keeps identities whole on purpose, with a comment saying why. The implementer reports 212 passed / 0 failed (206 at 64b9f94 plus 6 pins), no failing test in that run, and five mutation controls each red exactly one intended pin — their report, not my run. CI 44287567275 green on attempt 1, confirmed by `gh run list`. Cost read and not measured: over-budget values are hashed while the ring lock is held and a filter re-hashes per replayed event — a TASK-85 baseline question. The snapshot's unstated bound became TASK-97.
<!-- SECTION:NOTES:END -->
