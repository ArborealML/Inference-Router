---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T08:44:00Z'
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
Hosted CI on the reconciled PR #14 head wakes landing of the approved offline host-drain witness.
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
At 7ec1c20 use a real controller child, a surviving issuer and an independent backend terminal acknowledgment. Persist the exact stop and frozen successor before closing ingress. Serialize close and first-write, revoke prepared grants and account for every live incumbent route or prove there is only one. Bind the closed set to full launch, process, route, close ID and exact invocations/grants. Hold an authorized write before backend receipt. Count one issuer use; logical timeout is synthetic. Require both successful channel sealing and exact backend terminal evidence. Failed close, attempted disposal, mismatched acknowledgment and guard disposal grant nothing. Include one small terminal-first control alongside cleanup-first. Kill the controller, verify a new process and fresh nonce, and retain StopPrepared with zero stop/create ledger entries at every boundary while unresolved. Publish the immutable retired set under the issue fence without reopening. Recheck independent ownership and witness before dispatch; require fresh vacancy before one frozen successor create. Keep successor ingress closed.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #6](https://github.com/ArborealML/Inference-Router/pull/6) landed the checkpoint and synthetic ledger; [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) landed predecessor, exact stop-effect and fresh vacancy checks. Review of 47ab322 confirmed restart fencing and identified two weak observations. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) at c2db0ee passed all four affected Release cases with the probe rebuilt. The open control sends exact bytes with one use; closure denies an otherwise eligible grant. An independently backend-authored wrong grant retains the use after seal and blocks mutation. Matching acknowledgment permits retirement. Serving-process authenticity remains fixture-local. Independent correction review dispatched. Production enforcement and runtime admission remain separate.

Independent review of c2db0ee accepted all three dispositions, and hosted CI passed. GitHub refused the merge after MiniMax landed; merge-tree identified only SPEC.md. Retain the .80 header and ordered .80/.79 entries. Verify identical fixture blobs before advancing the PR.

Published df6174e preserves all seven reviewed project and fixture blobs from c2db0ee. Against its main parent the SPEC adds only the .79 entry and corrected scope under the existing .80 header. Both parent diff checks passed.
<!-- SECTION:NOTES:END -->
