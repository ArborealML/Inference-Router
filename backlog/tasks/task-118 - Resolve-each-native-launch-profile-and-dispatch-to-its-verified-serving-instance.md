---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-18T22:19:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-68
  - TASK-130
references:
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  - src/Arboreal.InferenceRouter/RouterCore.cs
ordinal: 116000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The selector includes launch profiles with identical model, context and slot counts but different effective settings. The current runtime owns one HTTP client per engine. Carry the resolved launch identity through verification and request delivery so an endpoint rebound to another process cannot receive work under its predecessor’s proof.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Aliases and omitted defaults resolve to the same effective target. Two profiles differing in an effective option remain distinct even when their model, context and slot counts match.
- [ ] #2 A joiner interprets its selection in the active claim’s captured configuration. A changed default or changed profile options in a newer configuration cannot silently retarget that claim.
- [ ] #3 For each supported engine family, fixtures verify all profile-distinguishing settings against one serving instance. Missing, conflicting or stale observations withhold readiness and prevent dispatch.
- [ ] #4 After an instance restart or port rebind between proof and dispatch, a paused waiter rechecks its reservation. Neither a stale pooled connection nor the replacement listener receives the request under the old proof.
- [ ] #5 A positive control delivers the expected upstream model ID to the verified instance. Tests distinguish an explicit profile request from ordinary inference fitting an already proven resident profile.
- [ ] #6 After the final readiness check, rebind the endpoint before the first upstream write. The replacement receives zero bytes under the previous proof. SPEC names the instance-bound transport or reservation and its retirement rule.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Verify catalog names and aliases resolve to one immutable target, including an alias that shadows a different canonical spelling. Ambiguous routing must yield zero writes and zero lifecycle mutations.

TASK-130 supplies verified-instance transport using the integrated host fixture. This task verifies that profile resolution and inference dispatch use it end to end. Production host closure consumes that evidence.
<!-- SECTION:NOTES:END -->
