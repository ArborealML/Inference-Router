---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: In Progress
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T07:52:00Z'
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
Implement the offline single-host drain witness before issuing a prepared incumbent stop. Production enforcement and ingress integration remain separate.
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
The checkpoint and synthetic ledger landed in [PR #6](https://github.com/ArborealML/Inference-Router/pull/6). [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) at b7a9ac9 passed 18 offline vacancy cases with the subprocess probe rebuilt. Review corrections bind the full predecessor and checkpoint stop effect, detect mismatched operation identity and stale owner evidence, and hold a competing facts writer through daemon entry. Merged at 47eaced after green hosted CI and independent review. Early owner refusals prove zero submission; downstream decision and terminal counts are separately pinned at the daemon-entry guard. Prepare the causal-drain and physical-retirement plan for the single host. Enforced host coverage, physical retirement and instance-bound admission remain prerequisites for production activation.

Plan review accepted the subprocess ownership split with corrections to the complete use set, two independent retirement conditions, restart ordering and issue-time checks. Direct transport exercises one physical owner. Runtime ticket accounting and issuer-crash recovery remain outside this increment.
<!-- SECTION:NOTES:END -->
