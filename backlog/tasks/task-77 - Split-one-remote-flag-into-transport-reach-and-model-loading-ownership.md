---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-20T20:11:00Z'
labels:
  - lifecycle
  - scheduling
milestone: m-6
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
Retain qualified lifecycle authority and incumbent restoration. Use the manager seam from TASK-206; prove managed peer acceptance/refusal and foreign occupancy exclusion. Test configured transport, bounded status, authenticated operation, disconnection, replay safety and AOT. No second pending queue or direct remote lifecycle commands.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Integrated lifecycle authority and incumbent recovery: commit 2f04f2b; prior 365/365 Release tests and Native AOT publish. The shipped-sample and authority tests remain evidence for legacy no-owner refusal (6263b83, 9/9; main CI 44704425802). TASK-128 owns concrete provider scope. These do not establish managed-peer activation or foreign occupancy exclusion. Retain conservative readiness withdrawal after uncertain mutation; preserve a valid incumbent after definitive pre-mutation refusal. No existing passed criterion was reset.

Historical [authority/recovery](https://github.com/ArborealML/Inference-Router/commit/2f04f2b), [shipped sample test](https://github.com/ArborealML/Inference-Router/blob/7350f71f9c8690e813339f0240ef2e752865f5fe/tests/Arboreal.InferenceRouter.Tests/ConfigLoaderTests.cs#L458-L469), [authority tests](https://github.com/ArborealML/Inference-Router/blob/2f04f2b/tests/Arboreal.InferenceRouter.Tests/LifecycleAuthorityTests.cs#L179) and [Docker provider tests](https://github.com/ArborealML/Inference-Router/blob/09a0d9b/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs#L578) retain their original scope.
<!-- SECTION:NOTES:END -->
