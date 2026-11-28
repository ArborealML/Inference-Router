---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-22T01:15:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-69
  - TASK-25
  - TASK-64
  - TASK-118
  - TASK-137
  - TASK-140
ordinal: 108000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Named local groups and same-host switch evidence shipped in PR #75; see [published verification](../../docs/local-model-switching.md#verification). After the pinned paired production admission/sender witness and TASK-206's seam, finish host authority, fencing and restart reconciliation within the dedicated inference resource boundary. External infrastructure establishes exclusive access to managed resources and isolates foreign launchers; AIR enforces ownership within that boundary and refuses unknown occupancy.
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
Initial launch and reconciliation return an explicit durable Applied receipt with ingress closed. Bind the exact checkpoint operation, plan and immutable instance to independent observation of effective settings and running state. Incomplete evidence keeps the effect fenced. Finish before route publication, Open or permit issuance; serving success is a separate outcome. Reuse PR #35 evidence. Future launch requires independently verified vacancy and exclusive mutation within the actual assigned resource domain, not a fixed GPU count or topology. Serving stop requires exact ownership, admission closure and confirmed physical retirement. Inference requires exact-generation Open and instance-bound first-write authorization. Keep the current resident untouched.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) qualifies offline retirement with a surviving issuer and independent backend acknowledgment.

Review required wrong-target fencing and old-grant/fresh-open controls.

[PR #19](https://github.com/ArborealML/Inference-Router/pull/19) preserves the paired 1/1 receipt at 6ef4723. The landed recovery closes stale incumbent authority before awaiting occupancy. Six focused cases passed. Deadline equality in the enrolled path remains separate from fixture cancellation.

[PR #23](https://github.com/ArborealML/Inference-Router/pull/23) binds the exact stored proof to the installed runtime attempt and full target before delivery. Review of 0f6245a found foreign proofs could pass on model/tier alone. Correction a99b91c passed independent review and 5/5 focused cases, including substitutions and a notification fault after Open. The deadline fixture passed at 8af4cdd and all seven guard/deadline cases at 8b6a919. Merged at 5d66c8d after all hosted checks passed. Production issuance still requires independently attested live authority.

Full commission passed review and 89/89 offline; isolated grant and approval mutations failed as expected. Provision rollback-independent floor. Future launch proof requires accepted create bytes, independent effective settings and exact running instance. Cutover requires exclusion of competing writers, devices and routes, vacancy, and actual readiness-handler evidence.

Decision-71 settles intended dedication, not actual exclusivity or cutover permission. Prove AIR enforcement with fixtures; external infrastructure supplies access controls. No general host provisioning or sandboxing.
<!-- SECTION:NOTES:END -->
