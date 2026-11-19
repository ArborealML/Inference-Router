---
id: TASK-68
title: >-
  Bind the serving gauges to a capability epoch, so a stale window cannot
  authorise a profile the engine cannot serve
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T01:11:00Z'
updated_date: '2026-11-19T08:07:00Z'
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
Publish model-bound window, slots and inferred profile in a versioned capability snapshot. Dispatch and fresh readiness enforce the ranked ticket’s explicit minimum, and each joined waiter evaluates its own floor.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The live gauge fields are gone: window, slots and inferred name live in the capability snapshot, and every reader takes one snapshot per decision
- [x] #2 A switch start raises the marker and sets health and serving unknown in one expected-version exchange, so a new model set never carries the old window
- [x] #3 An ordinary /props observation publishes only at the version it read and never while the marker is up
- [x] #4 A wait_health=false switch answers the window question from a fresh post-launch state read, never a pre-switch window
- [x] #5 Equal tuples publish nothing, one payload bumps once, and a known window becoming unknown publishes once
- [x] #6 A missing or stale serving window cannot authorize a profile requiring known capacity. Withdraw a previously wide observation and prove the narrower or unknown state governs the next decision.
- [x] #7 A waiter answers from the current snapshot, except that a marker raised by a later claim never withdraws a proof its own switch published (decision-36)
- [x] #8 A lost claim releases the lock and gate, re-decides within a named count and the wait deadline, and exhaustion returns the existing switch_failed 503
- [x] #9 A committed gauge change nudges dispatch outside the compare-exchange, no new event kind
- [x] #10 Profile-name inference is model-scoped, null on ambiguity, and never authorizes readiness
- [x] #11 Three deterministic pins, no new production seam: a stale wide /props response released after a narrow settle; a claim against a superseded version; a post-launch response reporting none
- [x] #12 After switching, a fresh model window below the explicit minimum prevents any write despite an unchanged tier label.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Publish the immutable serving tuple with EngineCapability. Convert scheduler, engine status, slot limit and settle readers. On switch start atomically publish the marker and unknown health/capacity. Fence ordinary publication by its captured version and marker. Trust-mode readiness reads fresh state whenever a named tier or explicit ticket minimum requires it. With neither, fresh model proof suffices. Carry the ranked ticket’s explicit option through all applicable exact takes and post-switch proof. Join two tickets whose explicit minima straddle one measured window: only the fitting ticket may write. Keep the prompt estimate for tier selection. Preserve decision-36 waiter semantics and bounded claim retries outside the locks; exhaustion returns switch_failed 503. Nudge dispatch after publication outside locks. Keep inferred profile model-scoped, null on ambiguity, and slots admission-owned. Document unknown capacity and window policy in SPEC 5.5, 7.6 and 15. The ordinary publisher follows TASK-76 criterion 2.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Ordinary publication and claim-scoped restoration landed in PRs #2 and #3. [PR #8](https://github.com/ArborealML/Inference-Router/pull/8) merged at a96b823. Sixteen integration cases covered winner-specific floors, route generation, live capacity, expiry, and joined delivery. The six corrected owner-handoff cases passed locally and [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44483866089) passed at 819535e. Independent review confirmed that the same waiting ticket sees the free permit before exact take and owns it after authorization, without a synthetic wake. CapabilityPublicationTests, SwitchClaimTests and the joint readiness tests cover the earlier snapshot and claim conditions. Enrolled instance verification remains with TASK-118.
<!-- SECTION:NOTES:END -->
