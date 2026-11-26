---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-26T02:37:00Z'
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
- [x] #1 A demand already satisfied by the resident tier never starts a switch.
- [x] #2 A pin reddens when a switch names the tier the engine is already serving.
- [ ] #3 Startup does not launch the default tier and then immediately switch away from it when a wider tier of the same window satisfies demand; carried by TASK-191.
- [ ] #4 Any switch that does start records the demand that required it in the feed with the target and the resident state; not delivered on this branch and needs its own slice.
- [ ] #5 A lifecycle lease is classified cold from trusted resident capacity, not from the tier label, so reusing a wider-lane resident costs zero provider calls.
- [ ] #6 A launch that reports more lanes than it was asked for fails launch conformance and never publishes Ready.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Outage containment only, one branch. Resolve the queued demand for a model, read a fresh same-generation engine identity with its model, measured window, measured slots and health under one synchronisation, and treat the resident as a zero-cost candidate target. When the resident is compatible, do zero Docker lifecycle work, emit no switch events, and bump no epoch. Rebinding an unadmitted ticket to the actual resident requires proving profile compatibility first: a measured capacity never silently rewrites a named profile. Pin the pair that proves direction: a verified 262144-window 8-slot resident with a stale narrow label must produce zero stops, deletes or recreates and must route correctly; a hand-replaced 256000-window 4-slot engine holding a stale 262144/8 gauge must NOT take the no-op path and must not admit an oversized ticket. Assert lifecycle calls, not feed text. The drain, lease lifecycle and startup launch-tier questions are separate plans and are not in this branch.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Delivered on branch fix/resident-tier-noop: test pair 1c6c821, fix 25bdeec. Satisfaction became window equality plus a lane floor with the inferred label dropped. External commit review covered 1c6c821..25bdeec (extra-high, 2026-11-26) and returned changes requested; I verified both blockers. (1) Containment does not hold for the local Docker group: LocalLifecycleGroup.cs:39-41 sets cold from profile.Name against the resident label, RouterCore.cs:500-507 reserves a lease even on a no-switch decision and :789-809 runs the cold path, so a named 4-lane tier against an 8-lane resident still stops and relaunches. Same-name-only reuse is the sole existing pin, which is why main looked green. (2) Launch conformance weakened: the same predicate judges non-enrolled launch readiness at EngineRuntime.cs:1950 and :2063, so expected-4 observed-8 now publishes Ready. Fix under way: split a satisfaction predicate from an exact-conformance one, plus the review's regression matrix — ambiguous or null label, stale gauge against a hand-replaced process between selection and forwarding, reload changing the roster mid-dispatch, no-ladder engines, and the offline verified exact-instance refusal at RouterCore.cs:479-481 kept name-exact and documented. Coordinator check on the delivered pair: 128 passed over five filters, build clean.
<!-- SECTION:NOTES:END -->
