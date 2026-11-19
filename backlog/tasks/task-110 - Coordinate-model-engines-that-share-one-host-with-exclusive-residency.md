---
id: TASK-110
title: Coordinate model engines that share one host with exclusive residency
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:13:00Z'
updated_date: '2026-11-19T06:33:00Z'
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
Wait for the dispatched independent review of the single-host vacancy plan. In the offline fixture verify exact incumbent ownership and retired use before stop. After StopApplied, obtain new independent evidence covering the enrolled partition and frozen successor. Recheck ownership, coverage and vacancy under the create fence. Distinguish refusal before submission from refusal before effect. On restart require a fresh query for an unsent successor and exact reconciliation of a dispatch that may already have taken effect. Production activation remains gated on enforced coverage, retirement and instance-bound admission.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The checkpoint and offline effect ledger are in [PR #6](https://github.com/ArborealML/Inference-Router/pull/6). At 4c0ceb1 the corrected 36-case ledger suite passed. Review accepted independent installed residency for launch, fenced rejection recovery and staged publication for trusted subprocess fixtures. The landing branch preserved the six reviewed C# files. Stop recovery checks its terminal evidence. Before real successor creation the host must independently establish vacancy and causal retirement. Unknown effects may remain pending after process death. Power-loss durability, hostile storage, and real serving authority remain unqualified. Existing terminal equality rejects noncanonical records. Further staging permutations are deferred until that protocol changes. Lifecycle is preparing the next single-host authority step.

The checkpoint and ledger landed at 508547f. Plan review dispatched against that merge; its verdict wakes the coordinator.
<!-- SECTION:NOTES:END -->
