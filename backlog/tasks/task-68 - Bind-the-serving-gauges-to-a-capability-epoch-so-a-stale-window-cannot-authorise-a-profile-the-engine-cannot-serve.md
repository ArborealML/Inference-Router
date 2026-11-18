---
id: TASK-68
title: >-
  Bind the serving gauges to a capability epoch, so a stale window cannot
  authorise a profile the engine cannot serve
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:11:00Z'
updated_date: '2026-11-18T04:57:00Z'
labels:
  - concurrency
  - scheduling
dependencies:
  - TASK-76
priority: high
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three serving gauges — window, slots, inferred profile — are live fields outside the capability snapshot, written by the health round with no version bump, while readiness answers everything else from a snapshot. A poll can widen a window between a claim's readiness read and its compare-exchange and license a reload of a window the engine already serves. decision-31 chose the shape; this row is the change.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The live gauge fields are gone: window, slots and inferred name live in the capability snapshot, and every reader takes one snapshot per decision
- [ ] #2 A switch start raises the marker and sets health and serving unknown in one expected-version exchange, so a new model set never carries the previous model's window
- [ ] #3 An ordinary /props observation publishes only at the version it read and never while the marker is up
- [ ] #4 A wait_health=false switch answers the window question from a fresh post-launch state read, never a pre-switch window
- [ ] #5 Equal tuples publish nothing, one payload bumps once, and a known window becoming unknown publishes once
- [ ] #6 The rule that an unreported window fits every profile either tightens or is recorded in SPEC 15 as an authorized gap
- [ ] #7 A waiter answers from the current snapshot, except that a marker raised by a later claim never withdraws a proof its own switch published (decision-36)
- [ ] #8 A lost claim releases the lock and gate, re-decides within a named count and the wait deadline, and exhaustion returns the existing switch_failed 503
- [ ] #9 A committed gauge change nudges dispatch outside the compare-exchange, no new event kind
- [ ] #10 Profile-name inference is model-scoped, null on ambiguity, and never authorizes readiness
- [ ] #11 Three deterministic pins with no new production seam: a stale wide /props response released after a narrow settle, a claim against a superseded version, a post-launch response reporting none
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
1. Add an immutable serving tuple to `EngineCapability`, delete the live fields, and convert every reader (`BuildEngineInfos`, `GetStats`, `EffectiveSlotLimit`, the settle path, the scheduler's view) to one snapshot each (decision-31).
2. Switch start publishes marker plus health unknown plus serving unknown in one expected-version exchange (decision-33).
3. Fence the ordinary state publisher by the version it read and by the marker; the switch keeps its own state read local and publishes one atomic proof of models, health and gauges, taking a fresh post-launch read on `wait_health = false`.
4. A waiter re-reads the snapshot, subject to decision-36; a lost claim releases the lock and the gate before re-deciding, bounded by a named count plus the wait deadline, exhausting to `switch_failed`/503 with an internal `claim_epoch_exhausted` reason (decision-34).
5. Nudge dispatch after a committed gauge publication, outside the compare-exchange; make profile-name inference model-scoped and null on ambiguity, with slots left an admission gauge (decision-35).
6. SPEC §5.5 and §7.6 wording for zero/unknown and capacity; §15 for the unreported-window policy and the slots-versus-settle discrepancy.

Ordered after the probe-marker-guard row (its AC #2 owns that guard).
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Plan review ingested before any code existed** (top tier, REVISE), findings checked against `3dce284`: the marker-blind negative publisher (`:235-247`) duplicates the guard row's own criterion and stays there; proof-before-window-check (`:1013` vs `:1038`), the claim loop re-deciding inside the gate (`:640-676`) and a waiter answering from its own ready boolean (`:782`, `:789`) are real. Unknown-publishes-once supersedes decision-31's first wording. Not adopted: a separate gauge generation, a monotonic-window shortcut, rollback.
<!-- SECTION:NOTES:END -->
