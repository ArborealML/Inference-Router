---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-18T18:40:00Z'
labels: []
dependencies:
  - TASK-69
ordinal: 108000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decision-25 requires a single resident model on this host. At 2d50430 each lifecycle coordinator has its own semaphore and Docker peer discovery checks the router and engine labels. Two engine names on the same host therefore need a shared reservation and an explicit ownership boundary. TASK-78 covers later fleet placement.
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

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design review reconciled against 6989f7c: the coordinator semaphore belongs to one instance and Docker discovery filters by router and engine. TASK-26 can exercise provider primitives independently against a fake daemon. Both are prerequisites for the qualified native swap in TASK-112. Reconcile durable proof against the runtime before transferring authority. Never infer vacancy from lease expiry.
<!-- SECTION:NOTES:END -->
