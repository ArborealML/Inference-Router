---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-26T01:53:00Z'
labels: []
dependencies:
  - TASK-25
priority: high
ordinal: 181000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live event feed records a switch to the profile 256k-8 at 17:40 on an engine that was already serving 256k-8 with a measured window of 262144 and 8 slots. The old container exited at that moment and a new one started; the switch took about two minutes, and every request in flight on the only healthy engine died with it. The same thing happened at 06:00 on this host, and at startup the router launched the model at the narrow-slot default tier and then immediately switched it to the wide-slot tier of the same window. A no-op target must be recognised before any lifecycle transaction begins.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A demand already satisfied by the resident tier never starts a switch.
- [ ] #2 A pin reddens when a switch names the tier the engine is already serving.
- [ ] #3 Startup does not launch the default tier and then immediately switch away from it when a wider tier of the same window satisfies demand.
- [ ] #4 Any switch that does start records the demand that required it in the feed with the target and the resident state.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Outage containment only, one branch. Resolve the queued demand for a model, read a fresh same-generation engine identity with its model, measured window, measured slots and health under one synchronisation, and treat the resident as a zero-cost candidate target. When the resident is compatible, do zero Docker lifecycle work, emit no switch events, and bump no epoch. Rebinding an unadmitted ticket to the actual resident requires proving profile compatibility first: a measured capacity never silently rewrites a named profile. Pin the pair that proves direction: a verified 262144-window 8-slot resident with a stale narrow label must produce zero stops, deletes or recreates and must route correctly; a hand-replaced 256000-window 4-slot engine holding a stale 262144/8 gauge must NOT take the no-op path and must not admit an oversized ticket. Assert lifecycle calls, not feed text. The drain, lease lifecycle and startup launch-tier questions are separate plans and are not in this branch.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementation in progress on branch fix/resident-tier-noop. Three implementer runs were lost to infrastructure (engine crash, provider outage, two 30-minute harness limits) before the first commit landed; the test pair is committed at 1c6c821 with the two pinned directions asserted on counted lifecycle calls. Coordinator review of the uncommitted fix found two defects routed back to the implementer: (a) the new SPEC §7.6 paragraph requires measured slots to EQUAL the declared tier while the changed ServingCapacity.Matches drops slot equality, so code and SPEC disagree; (b) the added TotalSlots >= 0 guard proves nothing and should go. Also routed: Matches is shared by PrefixAwareScheduler.cs:99, EngineRuntime.cs:986 and :2064, and RouterCore.cs:373 and :479, so widening it for request satisfaction also widens operator-facing exact-tier paths — either a separate exact predicate there, or a named check that the new semantics are intended. Commit 1c6c821 carries a Sol identity although the authoring seat ran on the q38fn implementer model; recorded rather than rewritten.
<!-- SECTION:NOTES:END -->
