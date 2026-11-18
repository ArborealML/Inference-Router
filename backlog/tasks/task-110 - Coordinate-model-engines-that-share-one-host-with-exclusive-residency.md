---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-18T20:21:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-69
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
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Configure an explicit physical host group and share one host-local arbiter across managed engines in that group. Persist the last admitted owner and epoch before side effects. Reconcile with authoritative provider observations after restart. Define how the sole mutation authority fences stale callers during outstanding provider operations. Separate occupied, vacant, foreign and unknown states. Hold the reservation across verified drain, stop, release, launch and readiness. Exercise restart at every external call boundary, delayed effects, unavailable storage and competing hosts with deterministic fakes. Coordinate admission and drain with TASK-25 and TASK-64.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan review verified the per-coordinator semaphore and engine-filtered discovery. Before a mutation, durably record its operation identity and immutable target. Prevent a successor epoch while the outcome is unknown. A fake daemon must accept an action, lose its response, and complete it after the router restarts. Verify the delayed action cannot affect a successor. Host-wide observation must detect foreign or unknown occupancy; independent hosts can progress. Direct writers outside the managed group require separate OS or daemon enforcement. Storage format is an implementation choice. Live engine operations remain parked.

Coordinator ran the host reservation, Docker and lifecycle filters against the first boundary candidate: 37 passed. Commit review dispatched. Cross-process restart, production occupancy reconciliation and runtime admission still require proof.
<!-- SECTION:NOTES:END -->
