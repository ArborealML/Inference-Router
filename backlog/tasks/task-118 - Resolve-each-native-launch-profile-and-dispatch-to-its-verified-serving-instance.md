---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-19T08:17:00Z'
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
Independent review of the single-host dispatch plan wakes implementation. Carry the resolved target and frozen request to the verified serving instance so a rebound endpoint cannot receive work under an earlier proof.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Aliases and omitted defaults resolve to the same effective target. Two profiles differing in an effective option remain distinct even when their model, context and slot counts match.
- [ ] #2 A joiner interprets its selection in the active claim’s captured configuration. A changed default or changed profile options in a newer configuration cannot silently retarget that claim.
- [ ] #3 For each supported engine family, fixtures verify all profile-distinguishing settings against one serving instance. Missing, conflicting or stale observations withhold readiness and prevent dispatch.
- [ ] #4 After an instance restart or port rebind between proof and dispatch, a paused waiter rechecks its reservation. Neither a stale pooled connection nor the replacement listener receives the request under the old proof.
- [ ] #5 A positive control delivers the expected upstream model ID to the verified instance. Tests distinguish an explicit profile request from ordinary inference fitting an already proven resident profile.
- [ ] #6 After the final readiness check, rebind the endpoint before the first upstream write. The replacement receives zero bytes under the previous proof. SPEC names the instance-bound transport or reservation and its retirement rule.
- [ ] #7 Freeze the final rewritten body and credential policy with the invocation. Changing either after preparation refuses delivery before the first write. An unchanged invocation sends the expected bytes to the verified route.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one real selected ticket, its captured catalog, exact admission and the existing issuer. Freeze the effective target, rewritten bytes and credential policy outside locks. At commitment recheck the ticket, route, deadline and capacity. Reject changed inputs before checkout. On submission retain the same physical use through confirmed exchange cleanup. Qualify explicit selection and fitting resident inference, stale identity, body or credential mismatch, and a held retirement against independent sinks. Use an injected resident with zero lifecycle mutations.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan dispatched for independent review against fe005df. Implementation starts after disposition. Family-specific readiness, durable production issuance and host activation remain separate. Confirm effective-plan identity, cleanup ownership and the mutation boundary with the assigned peers.
<!-- SECTION:NOTES:END -->
