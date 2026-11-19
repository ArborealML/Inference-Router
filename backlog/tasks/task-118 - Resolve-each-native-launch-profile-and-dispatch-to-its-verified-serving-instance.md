---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: Preparing
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-19T09:49:00Z'
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
Prepare the active-claim joiner check across two captured configurations. Review the single-resident Defiant authority prerequisite separately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Aliases and omitted defaults resolve to the same effective target. Two profiles differing in an effective option remain distinct even when their model, context and slot counts match.
- [ ] #2 A joiner interprets its selection in the active claim’s captured configuration. A changed default or changed profile options in a newer configuration cannot silently retarget that claim.
- [ ] #3 For each supported engine family, fixtures verify all profile-distinguishing settings against one serving instance. Missing, conflicting or stale observations withhold readiness and prevent dispatch.
- [ ] #4 After an instance restart or port rebind between proof and dispatch, a paused waiter rechecks its reservation. Neither a stale pooled connection nor the replacement listener receives the request under the old proof.
- [x] #5 A positive control delivers the expected upstream model ID to the verified instance. Tests distinguish an explicit profile request from ordinary inference fitting an already proven resident profile.
- [ ] #6 After the final readiness check, rebind the endpoint before the first upstream write. The replacement receives zero bytes under the previous proof. SPEC names the instance-bound transport or reservation and its retirement rule.
- [x] #7 Freeze the final rewritten body and credential policy with the invocation. Changing either after preparation refuses delivery before the first write. An unchanged invocation sends the expected bytes to the verified route.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Hold a claim under configuration A. Offer a joiner under B with a different default and effective setting at equal public capacity. Verify the join binds A or refuses before checkout. A fresh B claim must select the B digest. Identify the actual join and snapshot entry points before pre-code review. For Defiant, start with one existing resident of the verified pair. Bind the canonical full plan and launch receipt to immutable container and serving-process identity, then observe roster, measured context and total slots on the issuer-bound channel. Lifecycle owns closed-generation publication, issue and retirement. Owner drain/promotion and exact stop/ambiguous-effect fencing remain prerequisites to a managed swap.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #20](https://github.com/ArborealML/Inference-Router/pull/20) merged at 4537b1a after focused qualification, AOT and hosted CI. Its offline exact-ticket fixture verifies explicit launch, omitted default and ordinary resident reuse, exact rewritten bytes and credential, and mismatch refusal with zero checkout. AC5 and AC7 are satisfied at the injected issuer. AC1 has model alias/default and settings-digest evidence; [PR #21](https://github.com/ArborealML/Inference-Router/pull/21) carries the profile-alias witness. AC3 still requires same-instance evidence across families. AC4 and AC6 retain standalone rebind and exact-ticket first-write evidence; the real enrolled paused waiter remains open. Production activation requires host authority.
<!-- SECTION:NOTES:END -->
