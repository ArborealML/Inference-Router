---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-28T12:29:00Z'
labels:
  - lifecycle
  - scheduling
milestone: m-18
dependencies:
  - TASK-69
  - TASK-110
  - TASK-206
  - TASK-190
  - TASK-197
  - TASK-198
priority: high
ordinal: 77000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake after TASK-206's seam and TASK-110/190/197/198 physical ownership, recovery and conformance contracts. Preserve the shipped transport/lifecycle split and legacy resident forwarding. Finish managed-peer activation through the narrow authenticated manager contract, not remote container commands.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Config declares the inference endpoint and lifecycle owner independently. TOML and JSON tests pin legacy remote mapping for omitted, false and true.
- [ ] #2 An engine the router can reach but cannot start is switchable by a lifecycle provider that speaks to that host
- [x] #3 The router never stops or launches an engine whose lifecycle it does not own, pinned
- [x] #4 SPEC documents which requests the inference endpoint can serve and which lifecycle actions each owner can authorize.
- [x] #5 An omitted remote never grants load authority: ownership comes from a target-bound lifecycle provider, never from a hostname or from a missing key
- [x] #6 A shipped sample engine with no lifecycle block still refuses to switch after the migration, pinned by the sample test
- [ ] #7 A legacy remote endpoint is routable without actuation or adoption subject to the active mode's egress policy. A foreign endpoint sharing a managed host blocks activation until the host authority proves vacancy.
- [ ] #8 Remote and in-process managers use equivalent snapshot, accept/refuse, cancel and disposition semantics; stale readiness, stale writer incarnation or uncertain acceptance never grants ownership.
- [ ] #9 Router-only, manager-only and combined roles ship in the same Native AOT binary; manager is a leaf, with scoped authenticated transport and no competing pending queue.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Ship router, manager and combined roles in one Native AOT binary. Client ingress forwards to the singleton router. The manager offers bounded inventory, accepts or refuses activation or dispatch, reconciles prior operations and receives cancellation or delivery outcomes. After activation the router rechecks the original ticket’s priority, thread, capacity and exact resident before dispatch.

Bind writer, manager, domain generation, operation, ticket, recipe, projected bytes, credential revision and deadline. Persist acceptance before acknowledgment or effects. Before writing headers, verify local authority and commit possible use. Identical duplicates recover disposition. Conflicting bindings refuse. Fence late offers after cancellation. Missing records cannot prove nonacceptance across restart. Retain unresolved uses. Compact terminal records only behind a durable replay fence. Check client delivery and physical retirement separately.

Bound bodies, active streams, record windows and both hops’ byte credit. Reconnect cannot extend the deadline. Test one published hash as a router and two separate managers, then combined mode. Exercise warm and original queued cold requests, stale authority, foreign occupancy, duplicate and delayed offers, disconnect, held flush, partial write, actual process restart and exhausted retention. Count upstream writes, resident changes and caller terminals. Show the other domain progresses before recovery.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Preserve incumbents on definite pre-mutation refusal and withdraw readiness after uncertain mutation. Prior recovery qualification passed 365 Release tests and Native AOT. TASK-128 owns concrete provider scope.

Historical [authority/recovery](https://github.com/ArborealML/Inference-Router/commit/2f04f2b), [shipped sample test](https://github.com/ArborealML/Inference-Router/blob/7350f71f9c8690e813339f0240ef2e752865f5fe/tests/Arboreal.InferenceRouter.Tests/ConfigLoaderTests.cs#L458-L469), [authority tests](https://github.com/ArborealML/Inference-Router/blob/2f04f2b/tests/Arboreal.InferenceRouter.Tests/LifecycleAuthorityTests.cs#L179) and [Docker provider tests](https://github.com/ArborealML/Inference-Router/blob/09a0d9b/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs#L578) retain their original scope.

Review against 42f2369 and d84dccc identified late offers after cancellation, loss between acceptance and first write, and expiring dispositions as replay risks. The revised protocol and process tests cover each. Production acceptance still requires the executable sender and verified host authority.
<!-- SECTION:NOTES:END -->
