---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-18T23:15:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-69
  - TASK-25
  - TASK-64
  - TASK-118
ordinal: 108000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement exclusive residency for cooperating managed engines sharing a physical host group. The host mutation path retains authority through readiness and reconciles every uncertain provider operation after restart.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Two competing engine targets in one exclusive host group cannot launch concurrently or leave two models resident, demonstrated against fakes.
- [ ] #2 A handoff stops only a proven owned peer by immutable ID. Unknown ownership or uncertain stop refuses the launch.
- [ ] #3 Independent host groups can progress concurrently, and a failed handoff settles waiters under the TASK-69 failure policy.
- [ ] #4 A stable physical host group has a fenced reservation through preflight, drain, verified release, launch and readiness. Stale owners cannot act after restart or lease expiry.
- [ ] #5 A foreign or undiscoverable resident, failed ownership query or uncertain stop blocks launch. Missing-image preflight preserves the incumbent. Fake tests cover restart and stale discovery before launch.
- [ ] #6 Fleet placement cannot override a host refusal. State the exclusivity guarantee for cooperating launchers and the host enforcement required to extend it to outside processes.
- [ ] #7 After publication refusal or deadline expiry, dispose the drain guard and attempt admission. Neither the old nor the staged route can issue a permit. The exact applied launch remains recoverable.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Configure an explicit physical host group and share one host-local arbiter across managed engines in that group. Persist the last admitted owner and epoch before side effects. Reconcile with authoritative provider observations after restart. Define how the sole mutation authority fences stale callers during outstanding provider operations. Separate occupied, vacant, foreign and unknown states. Hold the reservation across verified drain, stop, release, launch and readiness. Exercise restart at every external call boundary, delayed effects, unavailable storage and competing hosts with deterministic fakes. Coordinate admission and drain with TASK-25 and TASK-64.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Record operation identity and immutable target before mutation. Unknown effects fence successor epochs. Detect foreign occupancy across the host; independent hosts can progress. External writers need separate enforcement. Injected runtime passed 133 focused and 475 Release tests plus server Native AOT. Offline restart integration passed 563 Release tests and Native AOT; diagnostic correction passed 57 focused tests. Corrected offline receipt fixtures passed 38 host coordinator tests, including independent create-attempt counting and mismatched applied receipts. Restart after publication but before acknowledgment requested a stop of the exact incumbent; the fake refused before any stop effect. Production must preserve exact-instance evidence through refusal and lost acknowledgment and recover without repeating mutation. Fixture review, launch-plan binding, storage durability, missing-instance recovery and physical permits remain open. Live operations remain parked.
<!-- SECTION:NOTES:END -->
