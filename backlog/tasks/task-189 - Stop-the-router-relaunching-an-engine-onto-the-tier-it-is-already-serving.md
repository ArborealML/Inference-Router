---
id: TASK-189
title: Stop the router relaunching an engine onto the tier it is already serving
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T18:00:00Z'
updated_date: '2026-11-26T08:35:00Z'
labels: []
dependencies:
  - TASK-25
priority: high
ordinal: 181000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The live feed records a switch to profile 256k-8 on an engine already serving 256k-8 with a measured window of 262144 and 8 slots. The old container exited, a new one started, the switch took about two minutes, and every request in flight on the only healthy engine died with it. The same happened at 06:00 on this host, and at startup the router launched the model at the narrow-slot tier and immediately switched it to the wide-slot tier of the same window. A no-op target must be recognised before any lifecycle transaction begins.
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
Branch fix/resident-tier-noop, pull request #85. Commits: 1c6c821 and 189bb46 test pair; 25bdeec named-take gate; 7ee6209 cold classification by measured capacity with the recorded label dropped; 447040e splits the predicates — window equality plus a lane floor for switch necessity and admission, window and lane count both equal at the two launch publication gates (Engines/EngineRuntime.cs:2064 and Engines/EngineRuntime.HostLifecycle.cs:154) — so a launch that observes extra lanes fails conformance instead of publishing Ready. My checks at 447040e: the two new pins pass, ResidentProfileNoOp 7/7, LocalLifecycleGroup 19/19 including the named-tier no-op pin, the lifecycle and scheduling filter 468/468 with the same 468 counted at the pre-change commit, build clean with warnings as errors. Wake: CI on pull request #85, the follow-up commit review over 7ee6209..447040e, and the criterion-4 slice that records demand, target and resident state for a switch that does start. SPEC v0.12.161 collides with the priority branch; the second integrator renumbers.
<!-- SECTION:NOTES:END -->
