---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-26T06:19:00Z'
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
- [x] #5 A lifecycle lease is classified cold from trusted resident capacity, not from the tier label, so reusing a wider-lane resident costs zero provider calls.
- [x] #6 A launch that reports more lanes than it was asked for fails launch conformance and never publishes Ready.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Outage containment only, one branch. Resolve the queued demand for a model, read a fresh same-generation engine identity with its model, measured window, measured slots and health under one synchronisation, and treat the resident as a zero-cost candidate target. When the resident is compatible, do zero Docker lifecycle work, emit no switch events, and bump no epoch. Rebinding an unadmitted ticket to the actual resident requires proving profile compatibility first: a measured capacity never silently rewrites a named profile. Pin the pair that proves direction: a 262144-window 8-slot resident with a stale narrow label must produce zero stops, deletes or recreates and must route correctly; a hand-replaced 256000-window 4-slot engine holding a stale 262144/8 gauge must NOT take the no-op path and must not admit an oversized ticket. Assert lifecycle calls, not feed text.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Branch fix/resident-tier-noop, pull request #85: 1c6c821 and 189bb46 test pair, 25bdeec named-take gate (window equality plus a lane floor, inferred label dropped), 7ee6209 cold classification by measured capacity — the local group no longer records a tier label, so a named 4-lane tier against an 8-lane resident costs zero provider calls. Wake: the extra-high commit review over 52aef12..7ee6209 and CI on that branch. My verification at 7ee6209: the pin that was red (3 expected versus 6 observed provider calls) passes, local lifecycle group 19/19, a lifecycle, drain, runtime, switch, capacity, managed-profile and scheduling sweep 468/468, build clean with warnings as errors. Both earlier blockers are closed: cold no longer reads the label, and launch readiness keeps its exact expected-and-observed proof, so satisfaction stays looser than publication. Still open: criterion 3 is the startup launch tier; criterion 4 needs its own slice so a switch that does start names demand, target and resident state; and whether two tiers sharing a window and lane count but differing in memory cost, such as an fp8 and an nvfp4 calibration, are distinguishable at all — that ambiguous-label pin is still owed. SPEC v0.12.161 collides with the priority branch; the second integrator renumbers.
<!-- SECTION:NOTES:END -->
