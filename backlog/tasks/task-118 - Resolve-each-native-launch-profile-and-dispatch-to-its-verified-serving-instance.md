---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: Waiting
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-28T12:07:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-68
  - TASK-130
  - TASK-206
  - TASK-195
references:
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  - src/Arboreal.InferenceRouter/RouterCore.cs
ordinal: 116000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-25 publishes its commissioned admission and retirement contract. Connect the verified transport to the existing host authority. The issuer and broker share durable issuance and revocation records and a monotonic cutoff. Bind the probe and credential to the exact serving process and require conclusive exchange retirement. The [launch record](https://github.com/ArborealML/Inference-Router/pull/49) identifies the container.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Aliases and omitted defaults resolve to the same effective target. Two profiles differing in an effective option remain distinct even when their model, context and slot counts match.
- [ ] #2 A joiner interprets its selection in the active claim’s captured configuration. A changed default or changed profile options in a newer configuration cannot silently retarget that claim.
- [ ] #3 For each supported engine family, fixtures verify all profile-distinguishing settings against one serving instance. Missing, conflicting or stale observations withhold readiness and prevent dispatch.
- [ ] #4 After an instance restart or port rebind between proof and dispatch, a paused waiter rechecks its reservation. Neither a stale pooled connection nor the replacement listener receives the request under the old proof.
- [x] #5 A positive control delivers the expected upstream model ID to the verified instance. Tests distinguish an explicit profile request from ordinary inference fitting an already proven resident profile.
- [ ] #6 After the final readiness check, rebind the endpoint before the first upstream write. The replacement receives zero bytes under the previous proof. SPEC names the instance-bound transport or reservation and its retirement rule.
- [x] #7 Freeze the final rewritten body and credential policy with the invocation. Changing either after preparation refuses delivery before the first write. An unchanged invocation sends the expected bytes to the verified route.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review on 084e3e0 ruled out the proposed two-ticket admission test as AC2 evidence. The runtime captures one config per engine and an installed readiness claim cannot receive configuration B. Injecting a second resolver reaches only per-ticket preparation. Ordinary omitted-tier reuse selects the resident tier, so both proposed rows exercise settings drift. Existing effective-input digest, wrong-target refusal before checkout, fresh-generation and positive delivery controls cover that outcome. Drop both rows and the optional substitute. Revisit AC2 when a real join can observe distinct snapshots.

Recover the executable bootstrap that selects an installed authority directory. Reuse its catalog and checkpoint verification, authenticated broker and partition reads, terminal listener and process-bound issuer. Feed the same authority through the normal host-manager factory. Both serving entry points must reject explicitly selected commissioning when the authority is missing or stale. Qualify positive startup through the shipping binary with controlled external services and prove the original queued request reaches the verified listener. An internal factory alone leaves this criterion open. Keep physical installation and live acceptance separately scoped.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #20](https://github.com/ArborealML/Inference-Router/pull/20) merged at 4537b1a after focused qualification, AOT and hosted CI. Its offline exact-ticket fixture verifies explicit launch, omitted default and ordinary resident reuse, exact rewritten bytes and credential, and mismatch refusal with zero checkout. AC5 and AC7 are satisfied at the injected issuer. AC1 has model alias/default and settings-digest evidence; [PR #21](https://github.com/ArborealML/Inference-Router/pull/21) landed the profile-alias witness at 5643b27. AC3 still requires same-instance evidence across families. AC4 and AC6 retain standalone rebind and exact-ticket first-write evidence; the real enrolled paused waiter remains open. TASK-25's assignee owns the paired ordinary-composition integration witness; this assignee delivers the bound sender and its component tests. Both production acceptance sets consume the same pinned witness. Exact open generation is required at mint and first write even if a waiter retains Ready.
<!-- SECTION:NOTES:END -->
