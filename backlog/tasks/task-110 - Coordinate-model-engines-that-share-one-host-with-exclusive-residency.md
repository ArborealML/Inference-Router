---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T18:13:00Z'
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
<!-- AC:END -->
