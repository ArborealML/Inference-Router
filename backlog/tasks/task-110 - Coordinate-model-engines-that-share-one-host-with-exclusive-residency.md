---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T08:23:00Z'
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
The lifecycle implementer is revising the offline host-drain fixture to distinguish grant revocation and independently observed backend completion.
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
The checkpoint and synthetic effect ledger landed in [PR #6](https://github.com/ArborealML/Inference-Router/pull/6). [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) qualified full predecessor, stop-effect and vacancy checks. [PR #14](https://github.com/ArborealML/Inference-Router/pull/14) at 52fe225 is CI-green with all seven fixture and project blobs identical to reviewed 47ab322 (2 drain and 18 affected vacancy cases passed locally). Review confirmed restart fencing and seal-plus-ack ordering. Correct two evidence gaps: show the same eligible grant can write when open and is refused after closure; construct the terminal from backend-owned channel/request identity and reject a mismatched backend-authored terminal. Limit serving-incarnation claims to fixture self-consistency. Independent production attestation remains with host and verified dispatch. Runtime enrollment and issuer-crash recovery remain separate.
<!-- SECTION:NOTES:END -->
