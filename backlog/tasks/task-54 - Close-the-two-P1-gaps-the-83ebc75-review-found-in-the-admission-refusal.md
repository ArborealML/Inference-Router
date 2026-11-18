---
id: TASK-54
title: Close the two P1 gaps the 83ebc75 review found in the admission refusal
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T19:47:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - scheduling
  - wire
  - concurrency
milestone: m-3
dependencies:
  - TASK-55
  - TASK-56
  - TASK-57
priority: high
ordinal: 54000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The commit review of 83ebc75 (the fast-refuse work) returned REVISE with two P1s. **20a06fa closed the second one and the P2
with it, and left the first open.** Closed: the multi-engine capability scan is now one immutable versioned snapshot per
engine, CAS-published at every write site, with the version sum required unchanged across the pass and an answer of "not
hopeless" when it will not settle; the scheduler's view takes its loaded list, switch marker and eligibility bit from one
read; and the refusal moved ahead of the queue cap. Still open, and now written in SPEC 5.5 as a limit rather than a gap:
tickets admitted while an engine's capability was **unknown** stay queued once absence becomes **known** — nothing revisits
them, so they hold queue positions and a later request for that model can be answered queue_overflow instead of
model_unavailable. The remedy touches how a queued ticket is re-evaluated, which is the same machinery TASK-51 would change;
that adjacency is why this stays a separate task rather than growing a fifth criterion. Plan review is in flight before code.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 queued tickets are refused model_unavailable once every engine is known and none can serve, pinned end to end with two queued tickets and no max_queue_time
- [x] #2 the refusal is evaluated against one coherent capability snapshot, pinned by a deterministic two-engine interleaving
- [x] #3 the scheduler view derives the loaded list and the eligibility bit from that one snapshot
- [ ] #4 SPEC 7 and 6.1 say what the code does, including what the feed does not carry
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Closed at 20a06fa** (AC #2, #3): CapabilityScanTests pins the coherence rule deterministically, and the capability read is one Volatile.Read(ref _cap) per engine per decision (Engines/EngineRuntime.cs:104-110). **Wire-visible posture, correct wherever it is restated:** an unservable model is refused 503 model_unavailable, not 429, and the feed carries one queue.rejected with reason model_unavailable and no request.enqueued. Queue overflow keeps 429 queue_overflow; the defect that overflow also emits request.enqueued is TASK-56, not this one.

**Wording this half has to get right (from the plan review of TASK-55):** at the head of a queue the hopeless-model decision
uses CapabilityScan.DefinitelyHopeless, not the scheduler's internal no_available_engine reason; and a ticket already queued
that becomes unserviceable emits **request.failed** with reason model_unavailable, not queue.rejected — queue.rejected is the
admission-time shape, and TASK-56 pins one event per refusal.

**Wake: TASK-55, TASK-56 and TASK-57 closing.** This half acts on the refusal predicate, so it only goes in once that predicate is accurate — a quiet-poll version, a gated switch transition, and a snapshot nothing can mutate underneath it. Until then the queued-ticket remedy would be written against a moving predicate.
<!-- SECTION:NOTES:END -->
