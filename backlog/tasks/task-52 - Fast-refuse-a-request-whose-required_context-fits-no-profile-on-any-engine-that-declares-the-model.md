---
id: TASK-52
title: >-
  Fast-refuse a request whose required_context fits no profile on any engine
  that declares the model
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-17T19:15:00Z'
updated_date: '2026-11-21T01:42:44Z'
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
Config owns one scheduler fixture correction shared by the .121 and .122 drafts. The built-in scheduler uses the configured tier ladder for the explicit-header refusal. An eligible engine without an applicable ladder precludes this refusal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the explicit-header case is refused before queueing, with a test naming the profile that would have been needed
- [ ] #2 SPEC says whether the estimated-context case is refused, and the reason is written where it can be disagreed with
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The 12 focused Release tests passed. With the prior admission source the intended rejection was queued; restoration passed both focused controls. Source review confirmed PrefixAwareScheduler is sealed, and the unrelated custom scheduler control passed. The build + test job failed at the Test step in [the .121 run at a3411c3](https://github.com/ArborealML/Inference-Router/actions/runs/44851109060) and [the dependent .122 run at 03a0c9e](https://github.com/ArborealML/Inference-Router/actions/runs/44851201897). Both reported Failed where SwitchCostDispatchTests.ACheapModelThatCannotFitTheRequestDoesNotEnterTheComparison expected Queued at line 67. Its explicit 8192-token floor exceeds the sole 1024-token tier, triggering the intended prequeue refusal. Use an oversized estimated prompt with the explicit floor omitted. Verify the ticket stays queued and B is selected, retain the explicit-floor admission controls, then verify the repaired .121 and refreshed .122 heads in hosted CI.
<!-- SECTION:NOTES:END -->
