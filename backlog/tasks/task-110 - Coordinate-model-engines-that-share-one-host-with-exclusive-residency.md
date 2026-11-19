---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T10:12:00Z'
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
Compose the single recovered-launch fixture on the pinned host and engine changes.
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
Review on 6177f17 confirmed the fake plan uses 1000 ms, ordinary enrollment settles before guard, and the staged bool fixture bypasses runtime. Persist a validated 100 ms B plan, independently vary caller wait. Lifecycle starts one original-policy budget before post-drain identity and reserves the exact closed generation before bound reads. Hand a pinned child to Shutdown for a fixture-scoped real RunSwitchAsync claim. Under host then transition check stop, identity and deadline before CAS; retry with unchanged budget. Commit proof, closed authority and terminal together. Return handled expiry or committed witness through later faults. Keep callbacks outside locks. Preserve completion/feed ordering with minting closed; after cleanup Open checks committed generation and live stop, then wakes outside locks. Check t99 success, withheld-timer t100 expiry with one health_timeout and no publication or duplicate failure, retained B and fresh same-ID recovery without duplicate effects. Production enrollment and capacity guards stay closed.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. Review of 47ab322 identified two weak observations. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) merged at 3160a11 after the reconciled df6174e passed hosted CI. This qualifies offline causal retirement with a surviving issuer and independent backend acknowledgment.

Review on 3160a11 required wrong-target recovery fencing and old-grant/fresh-open controls.

[PR #19](https://github.com/ArborealML/Inference-Router/pull/19) preserves the paired 1/1 receipt at 6ef4723. Review identified stale incumbent authority during applied-launch recovery and missing failure controls. Published e7b951c closes after the exact match and before the first occupancy await. Six focused Release cases passed, covering denial during and after a held probe, untouched preflight, both cancellation cuts, completion, installation and drain-disposal faults, and fresh retry without duplicate effects. Both findings cleared review, all hosted checks passed, and PR #19 merged at 0b84766. Deadline equality in the enrolled path remains separate from fixture cancellation.

The staged host half at 046ea91 was handed to Shutdown for the single integrated pair. Runtime child df9fdef is ready. Lifecycle owns the paired fixture. Host and runtime reviews dispatched; execution awaits composition.
<!-- SECTION:NOTES:END -->
