---
id: TASK-93
title: Bound every feed field at the funnel, and make SPEC true of it
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
updated_date: '2026-11-18T09:54:00Z'
labels:
  - wire
  - events
dependencies: []
priority: high
ordinal: 92000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The v0.12.37 cap sat on the event Model field and inside the engine emitter, so the ceiling §10 states for one feed reason was untrue of the request path failure producer, and a caller could choose how much text a feed record carried. That half shipped at 64b9f94. A high-tier review of that commit — every claim re-measured here — finds the same defect one layer out: §10 promises more than the mechanism delivers about identifier identity, and more than the code delivers about the caller own error body.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every producer that appends to the feed has its reason text held to the stated budget: a pin posts a 405-character model id through the request path failure producer and reads the stored reason at reporting length.
- [ ] #2 SPEC 10 is true of every producer as written, or SPEC narrows the claim to the producers that honour it — either way, in the same commit.
- [x] #3 Engine and profile names reaching a feed field or a caller error body sit at reporting length, matching the number SPEC states.
- [x] #4 A cut cannot split a surrogate pair, and the marker plus dropped-count text fits inside the reserve the cap claims.
- [x] #5 Two ids that become identical once cut stay distinguishable in a feed record — a bounded fingerprint or the ticket id — pinned by a two-id test.
- [ ] #6 The caller-visible error body is bounded at the exits that build it — the engine-unreachable exception text and the queue-overflow sentence — pinned at the exception or HTTP body a caller receives.
- [ ] #7 SPEC 10 states the fingerprint as a diagnostic distinguisher, collisions and literal-marker aliases possible, and the filter as head-matching rather than isolation.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Shipped at [64b9f94](https://github.com/ArborealML/Inference-Router/commit/64b9f94a677faf188b7f71c2e0fd7994ae7731c4) (SPEC v0.12.39): the funnel owns the four fields, the emitter second cut is gone, the request path cuts identifiers before the sentence, the marker fingerprints the whole value, backs off a surrogate pair and names its reserve (32); the identifier head is now 96, a wire-visible change. Open half: bound the caller-visible exits — Fail keeps the message raw and hands it to RouterRequestException (:828-850), engine_unreachable passes ex.Message (:660-663), OnQueueRejected interpolates an uncapped model id (:915), TryGetTicket returns Error, Model and ThreadId raw (:246-261) — and narrow the two §10 sentences AC #7 names.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review dispositions, each re-measured at 64b9f94 by this seat reading. AC #2 goes back: §10 promises a caller own error body bounded the same way, and it is not at the exits the plan names. AC #5 stays met — the criterion allowed a bounded fingerprint — but the distinguisher is FNV-1a folded to eight hex characters: 32 bits, so same-head ids collide for roughly one pair in four billion, and a filter value written literally as another id head-plus-marker passes Ident unchanged and matches it. TicketId and Kind stay uncapped by choice: TicketId is router-generated (:885) and exact lookups depend on it, and Append is public. Asked for in the pins: assert at most IdentifierChars, not IdentifierChars plus the reserve; a same-length pair in the request-path pin (405 and 410 differ in dropped count alone); a literal-marker alias case. Cost, read not run: over-budget values hash inside the ring lock, and a filter re-hashes per event — bounded by the ingress cap, so not its own row unless measured. The 206/0 count is the coding seat report; CI attempt 1 went red on TASK-87 pin, its rerun green.
<!-- SECTION:NOTES:END -->
