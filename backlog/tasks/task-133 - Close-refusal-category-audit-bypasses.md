---
id: TASK-133
title: Close refusal-category audit bypasses
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T18:50:00Z'
labels: []
dependencies: []
priority: high
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Merged [PR #10](https://github.com/ArborealML/Inference-Router/pull/10) at ada65a7. The syntax audit checks failed-result construction, record clones and recorded-reason writes in EngineRuntime.cs. The SPEC documents the readiness and empty-record fallback categories.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A negative control rejects changing the caller-facing failure detail through a record clone.
- [x] #2 A valid invocation or comment on the same line cannot mask an unclassified failure.
- [x] #3 Compound writes are rejected or the recorded reason is protected by construction. Document the existing HealthTimeout fallback for additional miss kinds.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use a centrally versioned test-only C# syntax parser. Walk invocations, writes, clones, construction and readiness switch arms. Restrict the bare factory exception to the recorded-reason fallback. Diagnose syntax errors separately. Test syntax-valid mutations by violation and line alongside valid same-line and comment controls.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Accepted the 1bf1263 guards for failed-result initializers and direct ref/out arguments. The [original CI failure](https://github.com/ArborealML/Inference-Router/actions/runs/44589611159) exposed an obsolete mutation anchor on the combined source. Independent review accepted 183ab7d on integrated head 35f694c: both invalid calls were caught and the benign expression passed. All 41 focused tests passed. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44591022134) passed 1,465 Release tests with zero skips, Linux Native AOT and the config smoke check. Behavioral tests cover the runtime mapping.
<!-- SECTION:NOTES:END -->
