---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T11:06:00Z'
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
The independent review of the future launch-record plan on 955436b wakes Lifecycle. Review dispatched before implementation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Two competing engine targets in one exclusive host group cannot launch concurrently or leave two models resident, demonstrated against fakes.
- [ ] #2 A handoff stops only a proven owned peer by immutable ID. Unknown ownership or uncertain stop refuses the launch.
- [ ] #3 Independent host groups can progress concurrently, and a failed handoff settles waiters under the TASK-69 failure policy.
- [ ] #4 A stable physical host group has a fenced reservation through preflight, drain, verified release, launch and readiness. Stale owners cannot act after restart or lease expiry.
- [ ] #5 A foreign or undiscoverable resident, failed ownership query or uncertain stop blocks launch. Missing-image preflight preserves the incumbent. Fake tests cover restart and stale discovery before launch.
- [ ] #6 Fleet placement cannot override a host refusal. State the exclusivity guarantee for cooperating launchers and the host enforcement required to extend it to outside processes.
- [x] #7 After publication refusal or deadline expiry, dispose the drain guard and attempt admission. Neither the old nor the staged route can issue a permit. The exact applied launch remains recoverable.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Identify and independently enroll a supervised Defiant broker with protected daemon access and ownership of the private observation endpoint. Its controller durably freezes the operation ID, epoch and intent before dispatch. Its worker persists acceptance before daemon I/O, verifies the instance and records the applied effect before acknowledgment. Authenticate lookup after reopen by immutable ID; fence uncertainty. Its observation ingress attests the connected serving process and stamps both roster and capacity reads on the same closed generation within the original budget. Strata supplies the payloads. Neither producer is wired. Verify covered vacancy, fencing and rollback protection before any separately authorized launch.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. Review of 47ab322 identified two weak observations. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) merged at 3160a11 after the reconciled df6174e passed hosted CI. This qualifies offline causal retirement with a surviving issuer and independent backend acknowledgment.

Review on 3160a11 required wrong-target recovery fencing and old-grant/fresh-open controls.

[PR #19](https://github.com/ArborealML/Inference-Router/pull/19) preserves the paired 1/1 receipt at 6ef4723. Review identified stale incumbent authority during applied-launch recovery and missing failure controls. Published e7b951c closes after the exact match and before the first occupancy await. Six focused Release cases passed, covering denial during and after a held probe, untouched preflight, both cancellation cuts, completion, installation and drain-disposal faults, and fresh retry without duplicate effects. Both findings cleared review, all hosted checks passed, and PR #19 merged at 0b84766. Deadline equality in the enrolled path remains separate from fixture cancellation.

[PR #23](https://github.com/ArborealML/Inference-Router/pull/23) binds the exact stored proof to the installed runtime attempt and full target before delivery. Review of 0f6245a found foreign proofs could pass on model/tier alone. Correction a99b91c passed independent review and 5/5 focused cases, including substitutions and a notification fault after Open. The deadline fixture passed at 8af4cdd and all seven guard/deadline cases at 8b6a919. Merged at 5d66c8d after all hosted checks passed. Production issuance still requires independently attested live authority.
<!-- SECTION:NOTES:END -->
