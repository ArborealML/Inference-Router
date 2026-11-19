---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Preparing
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T07:27:00Z'
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
- [ ] #7 After publication refusal or deadline expiry, dispose the drain guard and attempt admission. Neither the old nor the staged route can issue a permit. The exact applied launch remains recoverable.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Implement the reviewed offline partition observer. Require exact incumbent ownership and generation-bound retirement before stop. Bind each fresh observation to the full resource partition, owner and enrollment revisions, query, epoch, predecessor and frozen successor. Fence every modeled writer through create entry. Test a revision change followed by restoration of the original visible state. Missing preflight proof leaves zero successor sends, acceptances, attempts or effects. Refusal after submission may retain a send and acceptance but leaves zero attempts, effects or applied result. Reopen prepared stops and launches with fresh evidence and the original identities. Reconcile dispatching operations only by exact evidence. A recovered stop requires fresh vacancy before successor submission. A recovered launch requires its exact receipt and effect.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The checkpoint and synthetic ledger landed in [PR #6](https://github.com/ArborealML/Inference-Router/pull/6). [PR #9](https://github.com/ArborealML/Inference-Router/pull/9) at b7a9ac9 passed 18 offline vacancy cases with the subprocess probe rebuilt. Review corrections bind the full predecessor and checkpoint stop effect, detect mismatched operation identity and stale owner evidence, and hold a competing facts writer through daemon entry. Merged at 47eaced after green hosted CI and independent review. Early owner refusals prove zero submission; downstream decision and terminal counts are separately pinned at the daemon-entry guard. Prepare the causal-drain and physical-retirement plan for the single host. Enforced host coverage, physical retirement and instance-bound admission remain prerequisites for production activation.
<!-- SECTION:NOTES:END -->
