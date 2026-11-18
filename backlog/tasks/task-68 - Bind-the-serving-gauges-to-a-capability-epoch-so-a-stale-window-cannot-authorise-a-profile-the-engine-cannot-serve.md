---
id: TASK-68
title: >-
  Bind the serving gauges to a capability epoch, so a stale window cannot
  authorise a profile the engine cannot serve
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T01:11:00Z'
updated_date: '2026-11-18T21:20:00Z'
labels:
  - concurrency
  - scheduling
milestone: m-2
dependencies: []
priority: high
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three serving gauges — window, slots, inferred profile — are live fields outside the capability snapshot, written by the health round with no version bump, while every other readiness answer comes from a versioned snapshot. So a switch can authorise a profile on a window measured for the model loaded before this one. TASK-32 proved they are live from more than one place at once; this row puts them in the snapshot.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The live gauge fields are gone: window, slots and inferred name live in the capability snapshot, and every reader takes one snapshot per decision
- [ ] #2 A switch start raises the marker and sets health and serving unknown in one expected-version exchange, so a new model set never carries the old window
- [ ] #3 An ordinary /props observation publishes only at the version it read and never while the marker is up
- [ ] #4 A wait_health=false switch answers the window question from a fresh post-launch state read, never a pre-switch window
- [ ] #5 Equal tuples publish nothing, one payload bumps once, and a known window becoming unknown publishes once
- [ ] #6 A missing or stale serving window cannot authorize a profile requiring known capacity. Withdraw a previously wide observation and prove the narrower or unknown state governs the next decision.
- [ ] #7 A waiter answers from the current snapshot, except that a marker raised by a later claim never withdraws a proof its own switch published (decision-36)
- [ ] #8 A lost claim releases the lock and gate, re-decides within a named count and the wait deadline, and exhaustion returns the existing switch_failed 503
- [ ] #9 A committed gauge change nudges dispatch outside the compare-exchange, no new event kind
- [ ] #10 Profile-name inference is model-scoped, null on ambiguity, and never authorizes readiness
- [ ] #11 Three deterministic pins, no new production seam: a stale wide /props response released after a narrow settle; a claim against a superseded version; a post-launch response reporting none
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Immutable serving tuple in `EngineCapability`, live fields deleted, every reader converted (`BuildEngineInfos`, `GetStats`, `EffectiveSlotLimit`, the settle path, the scheduler's view).
2. Switch start publishes marker, health-unknown and serving-unknown in one expected-version exchange (decision-33).
3. Fence the ordinary publisher by the version it read and by the marker; the switch publishes one atomic proof, taking a fresh post-launch read when `wait_health = false`.
4. A waiter re-reads the snapshot (decision-36); a lost claim releases lock and gate before re-deciding, bounded by a named count and the wait deadline, exhausting to `switch_failed` 503.
5. Nudge dispatch after a committed publication, outside the compare-exchange; inference model-scoped, null on ambiguity; slots stay an admission gauge (decision-35).
6. SPEC 5.5 and 7.6 for zero/unknown and capacity; 15 for the unreported-window policy and the slots-versus-settle gap.

Ordered after TASK-76 (criterion #2).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At eba81d4, WindowFits accepted a zero gauge and an omitted key preserved a stale positive. Bind each measurement to its serving epoch and withhold capacity-dependent admission when current evidence is missing.

The TASK-76 prerequisite was its marker-held proof, now verified in [the integration](https://github.com/ArborealML/Inference-Router/commit/fcff3ea). The remaining refusal-path review can proceed independently.

At fcff3ea a fake-engine test called WaitForProfileAsync for 256000 tokens with the discovery window fixed at 256000 and static capability at 131072. It incorrectly returned Ready both when the serving window was always missing and when a previously wide observation lost its window. The same model remained eligible without a required profile.
<!-- SECTION:NOTES:END -->
