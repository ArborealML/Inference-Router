---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T07:38:00Z'
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
Independent review of the causal-drain fixture plan wakes implementation. Establish exclusive residency for cooperating engines sharing one host and reconcile uncertain mutations after restart.
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
Use combined base 7ec1c20 for one offline handoff. Hold an authorized application write before backend delivery. Close submission and capture the full use set under the same ordering. A logical timeout and an expired drain leave one physical use and zero stop or create. Restart only the controller. Query the surviving issuer and backend; reject mismatched generation, local close and incomplete cleanup. Bind terminal evidence to the invocation, full predecessor, process, route generation and closed set. Retire once after exact terminal and cleanup. A newly authorized drain may then issue the exact stop and frozen successor create through existing ledgers. Keep successor ingress closed. Dispatch and production authority have separate prerequisites.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The checkpoint and synthetic ledger landed in [PR #6](https://github.com/ArborealML/Inference-Router/pull/6). [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) at b7a9ac9 passed 18 offline vacancy cases with the subprocess probe rebuilt. Review corrections bind the full predecessor and checkpoint stop effect, detect mismatched operation identity and stale owner evidence, and hold a competing facts writer through daemon entry. Merged at 47eaced after green hosted CI and independent review. Early owner refusals prove zero submission; downstream decision and terminal counts are separately pinned at the daemon-entry guard. Prepare the causal-drain and physical-retirement plan for the single host. Enforced host coverage, physical retirement and instance-bound admission remain prerequisites for production activation.
<!-- SECTION:NOTES:END -->
