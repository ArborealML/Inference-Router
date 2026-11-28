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
Pi's explicit per-request priority is fixed for that ticket; changing a thread reorders inherited pending keys only. TASK-185 owns client command/wire behavior; do not duplicate parsing from PR #86.

Candidate f0f110e2fe96674a10dd2e8270286751181f2193 implements AC1–4. Independent pre-code review required attached-handle/throwing-observer coverage; source range 6b72a75..f0f110e passed. Release verification at exact f0f110e: 105 passed, zero failed/skipped across the eight planned groups; build/analyzers passed. CA1861 was fixed without suppression or weaker assertions.

Combined 203ce78 exposed an intermittent fixture timeout: TryDispatchOnce can report StateChanged without admission after prior logical cleanup invalidates its observation. The corrected fixture waits for actual release, checks exact selection and deterministically proves that interleaving with existing hooks. No runtime change or deadline increase. Integration 02087978 independently reviewed; 608 affected tests passed at that exact head. Stack review base remains TASK-203 receipt 45ba972. Coordinator next publishes and checks exact-head hosted build/test/AOT. No main merge or production/Done claim; Backlog CLI unavailable.
<!-- SECTION:NOTES:END -->
