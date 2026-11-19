---
id: TASK-33
title: >-
  Document that the native Docker lifecycle provider is verified only against
  fakes
status: Done
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-17T07:53:00Z'
updated_date: '2026-11-19T02:53:00Z'
labels:
  - engine
  - lifecycle
  - security
milestone: m-4
dependencies: []
modified_files:
  - SPEC.md
priority: medium
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The initial qualification used fake providers and Docker HTTP responses. Decision-68 subsequently authorized primary-host selector engines for live qualification under TASK-112.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the router does not drive the live Docker daemon on this host: the answer is written into SPEC 5.3.1 as a posture, with the refuse-don't-escalate rule (decision-1) named
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
SPEC §5.3.1 in [05fe144](https://github.com/ArborealML/Inference-Router/commit/05fe144) names the fake-only verification posture and implements the decision-1 documentation requirement: unavailable or unauthorized daemon access is refused without escalation. The [owner parking record](https://github.com/ArborealML/Inference-Router/commit/2d50430) superseded the provisional window. TASK-112 carries any future explicitly authorized live swap: identify an idle engine, verify drain and replacement, record affected resources and measure each phase.
<!-- SECTION:NOTES:END -->
