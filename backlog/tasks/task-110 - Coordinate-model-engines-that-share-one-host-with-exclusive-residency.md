---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T09:42:00Z'
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
The independent deadline-plan verdict and Shutdown’s source readback wake the coordinator to release the offline equality fixture.
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
Plan review dispatched on 6177f17. Resolve the timeout from the exact retained launch. Start one monotonic attempt before the post-drain identity check and carry it through observation, durable staging and final publication. Acquire host admission then transition lock. Check stop and deadline equality immediately before the capability CAS and exact closed-authority commit; recheck on CAS retry using the original budget. State changes under the guards must be callback-free. Settle once, with hooks and completion outside both locks. With qualifying evidence at 99 ms and timer delivery withheld, reach 100 ms after staging: refuse proof, Complete, Open and writes, retain applied B, then recover the same ID on a fresh authorized attempt and send once without repeating effects. Keep activation closed.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. Review of 47ab322 confirmed restart fencing and identified two weak observations. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) at c2db0ee passed all four affected Release cases with the probe rebuilt. The open control sends exact bytes with one use; closure denies an otherwise eligible grant. An independently backend-authored wrong grant retains the use after seal and blocks mutation. Matching acknowledgment permits retirement. Serving-process authenticity remains fixture-local. Independent correction review dispatched. Production enforcement and runtime admission remain separate.

[PR #14](https://github.com/ArborealML/Inference-Router/pull/14) merged at 3160a11 after the reconciled df6174e passed hosted CI. This qualifies offline causal retirement with a surviving issuer and independent backend acknowledgment.

Plan review on 3160a11 identified the legacy wrong-target recovery fallback and required a discriminating old grant and fresh-open positive. All four findings incorporated; lifecycle released to implement.

[PR #19](https://github.com/ArborealML/Inference-Router/pull/19) preserves the paired 1/1 receipt at 6ef4723. Review identified stale incumbent authority during applied-launch recovery and missing failure controls. Published e7b951c closes after the exact match and before the first occupancy await. Six focused Release cases passed, covering denial during and after a held probe, untouched preflight, both cancellation cuts, completion, installation and drain-disposal faults, and fresh retry without duplicate effects. Both findings cleared review, all hosted checks passed, and PR #19 merged at 0b84766. Deadline equality in the enrolled path remains separate from fixture cancellation.
<!-- SECTION:NOTES:END -->
