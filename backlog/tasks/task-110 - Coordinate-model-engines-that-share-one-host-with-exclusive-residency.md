---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-18T19:02:00Z'
labels: []
dependencies:
  - TASK-69
ordinal: 108000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when the implementation plan review returns. A host shared by multiple managed engines needs one reservation through readiness. Each engine currently has its own lifecycle semaphore, and Docker peer discovery filters by router and engine labels. TASK-78 handles fleet placement.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Two competing engine targets in one exclusive host group cannot launch concurrently or leave two models resident, demonstrated against fakes.
- [ ] #2 A handoff stops only a proven owned peer by immutable ID. Unknown ownership or uncertain stop refuses the launch.
- [ ] #3 Independent host groups can progress concurrently, and a failed handoff settles waiters under the TASK-69 failure policy.
- [ ] #4 A stable physical host group has a fenced reservation through preflight, drain, verified release, launch and readiness. Stale owners cannot act after restart or lease expiry.
- [ ] #5 A foreign or undiscoverable resident, failed ownership query or uncertain stop blocks launch. Missing-image preflight preserves the incumbent. Fake tests cover restart and stale discovery before launch.
- [ ] #6 Fleet placement cannot override a host refusal. State the exclusivity guarantee for cooperating launchers and the host enforcement required to extend it to outside processes.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Configure an explicit physical host group and share one host-local arbiter across managed engines in that group. Persist the last admitted owner and epoch before side effects. Reconcile with authoritative provider observations after restart. Define how the sole mutation authority fences stale callers during outstanding provider operations. Separate occupied, vacant, foreign and unknown states. Hold the reservation across verified drain, stop, release, launch and readiness. Exercise restart at every external call boundary, delayed effects, unavailable storage and competing hosts with deterministic fakes. Coordinate admission and drain with TASK-25 and TASK-64.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design review reconciled against 6989f7c: the coordinator semaphore belongs to one instance and Docker discovery filters by router and engine. TASK-26 can exercise provider primitives independently against a fake daemon. Both are prerequisites for the qualified native swap in TASK-112. Reconcile durable proof against the runtime before transferring authority. Never infer vacancy from lease expiry.

Implementation plan submitted for external review on 2026-11-18 with the native provider source and admission contracts. Check crash recovery when a daemon operation outlives its caller.
<!-- SECTION:NOTES:END -->
