---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T09:18:00Z'
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
Prepare the qualified closed-route staging change for integration on current main while its bounded review runs.
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
Fence wrong target or missing publication before legacy recovery can settle or grant. Bind original launch-owned plan, operation, instance and epoch. Finish fallible staging while closed. Retain the applied record through refusal and success. Disposal grants nothing. Only successful completion followed by explicit Open of the still-current generation authorizes inference. In one paired fixture first send on the eligible old route and hold another unused grant. Decline a valid publication and verify zero new permits/bytes, then probe wrong target without relaunch. Recover with a new coordinator, verify no mint through Complete or stale Open, and send once after fresh matching Open with one create total. Keep injection internal; runtime deadline, dispatch, admission and production activation remain separate.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. Review of 47ab322 confirmed restart fencing and identified two weak observations. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) at c2db0ee passed all four affected Release cases with the probe rebuilt. The open control sends exact bytes with one use; closure denies an otherwise eligible grant. An independently backend-authored wrong grant retains the use after seal and blocks mutation. Matching acknowledgment permits retirement. Serving-process authenticity remains fixture-local. Independent correction review dispatched. Production enforcement and runtime admission remain separate.

[PR #14](https://github.com/ArborealML/Inference-Router/pull/14) merged at 3160a11 after the reconciled df6174e passed hosted CI. This qualifies offline causal retirement with a surviving issuer and independent backend acknowledgment.

Plan review on 3160a11 identified the legacy wrong-target recovery fallback and required a discriminating old grant and fresh-open positive. All four findings incorporated; lifecycle released to implement.

Closed-route staging at 6ef4723 passed its paired Release case (1/1). Refusal retained LaunchApplied and kept grants closed after disposal. Recovery reopened a fresh completed generation for one exact send with one create and one stop overall. Bounded commit review dispatched. Prepare SPEC .82 on the landed GLM base.
<!-- SECTION:NOTES:END -->
