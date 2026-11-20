---
id: TASK-109
title: >-
  Make the subscriber-isolation pins separate the claims they name, and add the
  two missing controls
status: To Do
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-18T17:58:00Z'
updated_date: '2026-11-20T20:45:00Z'
labels:
  - tests
  - events
milestone: m-2
dependencies: []
priority: medium
ordinal: 107000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
[SPEC §6.1 subscriber I/O responsibilities](https://github.com/ArborealML/Inference-Router/pull/56) landed at ebcca83 and [main CI passed](https://github.com/ArborealML/Inference-Router/actions/runs/44813198939). The [joined-waiter test](https://github.com/ArborealML/Inference-Router/pull/55) landed at 2c43490. It records the same completion sequence at the producer commit and both wakes, with one gated launch, two pending callers and exactly one matching subscriber fault. Ordering, fanout and counter mutations produced 1, 2 and 4 intended failures. All 19 focused cases passed after restoration and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44808078550) passed all three jobs. The per-run load measurement remains open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A pin faults a throw at the terminal emit (`engine.switch_completed`), not only at `switch_started`, and asserts that emit's seq is in the ring before either caller completes (decision-7's feed-precedes-wake order)
- [x] #2 The stranding pin proves the join, not only the answer: exactly one launch (`LaunchCount == 1`) and the joined caller still incomplete while the launch is gated
- [x] #3 The producer pin asserts a fault delta of exactly one for the event it targets, not a nonzero total that any other event could satisfy
- [x] #4 Two further controls redden named pins with traces: a `break` where the catch continues reddens the surviving-subscriber assertion, and a no-op count reddens the count and status pins
- [x] #5 SPEC 6.1's subscriber rule names what an observer that performs I/O owes for its own failure, so the ring's catch is not read as the only boundary
- [ ] #6 Counts are re-measured at the tree with per-run load recorded, and no pin is added to the two classes TASK-87 holds open
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Hold one launch until both callers are enrolled. Throw only on the matching switch_completed event. At the existing producer commit hook and both normal wake observations, store immutable snapshots before signaling. After settlement, check that all three snapshots contain the same terminal sequence. Reuse the existing ring and status controls and show the producer assertion catches publication after settlement.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Origin: the sol-pro review of `2b751ec`, ingested 17:58Z. Its three loudest findings do **not** apply as written, and the read that settles them is the subscriber list: production has two subscribers — the dispatcher's wake nudge (`RouterCore.cs:71`) and one admin feed hand-off (`RouterCore.cs:305-316`), which copies events into a bounded channel with `TryWrite`, never touches the network inside the ring, and ends the stream when its buffer fills. No subscriber in this tree writes a partial SSE frame, so a half-open response reached from the ring catch is not reachable today, and one global counter covers the two observers that exist. The ordering finding is coverage, not code: every `Emit` sits in the switch body (`EngineRuntime.cs:1103`, `:1137`) and the waiter settles at `:1055`, so the order holds structurally and has no pin. My excerpt caused the mismatch — it named the status field and the rule but not the subscriber call sites; the next payload hands over the call sites with the rule.
<!-- SECTION:NOTES:END -->
