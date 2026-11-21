---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: Waiting
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-21T02:46:16Z'
labels:
  - scheduling
  - wire
milestone: m-3
dependencies: []
priority: high
ordinal: 52000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on retargeted [PR #72](https://github.com/ArborealML/Inference-Router/pull/72) against recovered main and its exact CI. The corrected scheduler fixture passed 78 focused Release tests. The built-in scheduler uses the configured tier ladder for the explicit-header refusal. An eligible engine without an applicable ladder precludes this refusal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The 12 focused Release tests passed. With the prior admission source the intended rejection was queued; restoration passed both focused controls. Source review confirmed PrefixAwareScheduler is sealed, and the unrelated custom scheduler control passed. The build + test job failed at the Test step in [the .121 run at a3411c3](https://github.com/ArborealML/Inference-Router/actions/runs/44851109060) and [the dependent .122 run at 03a0c9e](https://github.com/ArborealML/Inference-Router/actions/runs/44851201897). Both reported Failed where SwitchCostDispatchTests.ACheapModelThatCannotFitTheRequestDoesNotEnterTheComparison expected Queued at line 67. Its explicit 8192-token floor exceeds the sole 1024-token tier, triggering the intended prequeue refusal. Use an oversized estimated prompt with the explicit floor omitted. The fixture kept A queued while B dispatched. Both repaired heads passed hosted CI: [.121 at db1c8b5](https://github.com/ArborealML/Inference-Router/actions/runs/44852036431) and [.122 at 8fee6c6](https://github.com/ArborealML/Inference-Router/actions/runs/44852035715).
<!-- SECTION:NOTES:END -->
