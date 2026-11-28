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

Candidate 786c524e29c2938d92adfc7d0ed3868c6043a968 implements AC1–4. Independent pre-code review required attached-handle/throwing-observer coverage; implemented and independently reviewed source range 6b72a75..786c524 passed with no blocking findings. Release verification at exact 786c524: 104 passed, zero failed/skipped across the eight planned groups; build/analyzers passed. One test-only CA1861 failure was corrected by reusing the same expected array, without suppression or weaker assertions.

Stacked prerequisite TASK-203 9f023f30e9b5660bafed64c8fccd1b9f3accf3a7 is independently reviewed with 263 tests passing; review this increment against receipt base 45ba972. Coordinator next verifies the combined scheduling/observer/admin branch, publishes the focused stack and checks actual-head hosted build/test/AOT. No main merge, production/client qualification or Done claim. Backlog CLI unavailable; controlled Markdown and strict checker substitute.
<!-- SECTION:NOTES:END -->
