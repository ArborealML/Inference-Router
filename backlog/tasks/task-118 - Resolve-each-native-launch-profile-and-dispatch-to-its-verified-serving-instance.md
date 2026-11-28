---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: Preparing
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-28T13:48:00Z'
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
Prepare the separate-process acceptance run for the shipping server and CLI. Exercise checked incumbent attachment, the original queued successor, a rebind before first write and physical retirement under distinct authorities. Installation and access control belong to the deployment owner.
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
Review at 084e3e0 found the proposed two-ticket fixture exercises settings drift. Each runtime binds one configuration, and an omitted tier reuses the resident tier. Existing digest, wrong-target, fresh-generation and positive-delivery controls cover drift. Revisit AC2 when a real join can observe distinct snapshots.

Recover the executable bootstrap that selects an installed authority directory. Reuse its catalog and checkpoint verification, authenticated broker and partition reads, terminal listener and process-bound issuer. Feed the same authority through the normal host-manager factory. Both serving entry points must reject explicitly selected commissioning when the authority is missing or stale. Qualify positive startup through the shipping binary with controlled external services and prove the original queued request reaches the verified listener. Verify inherited listener activation and peer identity, attach the proven incumbent, and bind its cutoff before the first stop. Reconcile one durable operation through stop and create. Missing readback keeps admission closed. Exercise actual binary HTTP delivery; host provisioning remains separate.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[Exact-ticket qualification](https://github.com/ArborealML/Inference-Router/pull/20) covered injected issuance, explicit and default launches, resident reuse, exact bytes and credentials and zero checkout on mismatch. [Profile aliases](https://github.com/ArborealML/Inference-Router/pull/21) qualified at 5643b27. Same-instance proof across families remains open. TASK-25 owns the paired production witness.

[Stock serving and broker recovery](https://github.com/ArborealML/Inference-Router/pull/103) merged at cd069cc. The protected socket and disk qualification passed 235 tests. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46150598776) passed 2,486 tests and Native AOT. Review at 961e109 confirmed bounded replay, recovery before and after broker opening, reconciliation of failed head updates and release of definitely unissued probes. The process qualification plan is in independent review. Installed host and multi-host acceptance remain open. TASK-190 owns reconciliation of a successor operation whose journal append outlives a lost head update.
<!-- SECTION:NOTES:END -->
