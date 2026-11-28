---
id: TASK-205
title: "Recompute inherited priority for queued requests"
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-27'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 205000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Enqueue copies inherited priority into immutable pending nodes; updating the thread resorts stale values and registration uses a different update/wake path. Fix in place. TASK-185 owns Pi and wire parsing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Promotion and demotion reorder inherited pending requests; explicit overrides and FIFO enqueue sequence are unchanged.
- [ ] #2 Register/update share the same mutation, event and wake contract. Selection racing an update revalidates the exact pending candidate before admission.
- [ ] #3 Priority changes neither inflight cap nor active work, provider score, spend, context, admission target or physical lanes.
- [ ] #4 SPEC §§5.1 and 5.5 and focused race tests pin ordering and terminal behavior.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Replace inherited immutable pending-priority nodes under existing router→queue ordering; preserve ticket, enqueue sequence, age and explicit overrides. Sort affected queues only. Unify existing-thread registration and SetThreadPriority mutation/event/wake behavior, including deferred/reentrant publication and guaranteed wake when an observer throws.

Route attached public RouterThread.SetPriority through the same small owner callback. Keep standalone handles usable and reject stale/detached-handle mutation of a replacement identity. Exact take revalidates live competing order; only the measured-cost cohort gets a pending-priority revision fence, without a global wake veto. No inflight, provider economics, physical selection or spend changes.

Test inherited promotion/demotion, explicit overrides and FIFO, registration/attached/direct mutation, repeated and reentrant updates, throwing observers, stale handles, and updates racing exact take/cost selection. Run QueuedPriorityTests, ModelQueueTests, QueueDispatchTests, IndependentProgressTests, ConfigReloadRaceTests, SwitchCostDispatchTests, TicketLifetimeTests and ThreadPriorityTests. Update SPEC §§5.1/5.5/14 at v0.12.165 and README.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #91](https://github.com/ArborealML/Inference-Router/pull/91), head 658c213e09a9ce75fcec3b3b9eda05558c8fd026, stacked on PR #88 at cb595b2d96646fe038261ff57f6041e813aacd6f. [Mappings](../milestones/wave1-publication-map.json) preserve reviewed 6b72a75..f0f110e and the exact prepared/published trees under TASK-220's provenance exception.

AC1–4 source implemented. Pre-code review required attached-handle/throwing-observer coverage; corrected source review passed. Local Release at f0f110e passed 105 tests, zero failures/skips; build/analyzers passed. CA1861 was fixed without suppression. Earlier integration 203ce78 exposed a fixture that equated StateChanged with admission and raced logical cleanup. Actual release/exact-selection checks and a deterministic existing-hook regression corrected it without runtime changes or longer deadlines. Corrected integration 02087978 passed 608 affected tests.

Pi's explicit per-request priority stays fixed; only inherited pending keys reorder. TASK-185/PR #86 retain client/parser ownership. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112352768) passed on this head: 2,166 tests, zero failed/skipped, build, Linux AOT/config smoke, strict board and release harness. Next: reconcile after #88 lands and obtain final integration approval. No merge/live/Done claim; Backlog CLI unavailable.
<!-- SECTION:NOTES:END -->
