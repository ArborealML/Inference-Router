---
id: TASK-133
title: Close refusal-category audit bypasses
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T18:44:00Z'
labels: []
dependencies: []
priority: high
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI on [PR #10](https://github.com/ArborealML/Inference-Router/pull/10) and the bounded anchor review wake the landing decision. In the [prior failed job](https://github.com/ArborealML/Inference-Router/actions/runs/44589611159/job/135791730275), three mutations missed their source anchor on the merge tree. Child 183ab7d targets the current conditional branch and preserves the standalone historical return. Integrated head 35f694c passed all 41 focused Release cases with zero skips.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A negative control rejects changing the caller-facing failure detail through a record clone.
- [ ] #2 A valid invocation or comment on the same line cannot mask an unclassified failure.
- [ ] #3 Compound writes are rejected or the recorded reason is protected by construction. Document the existing HealthTimeout fallback for additional miss kinds.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use a centrally versioned test-only C# syntax parser. Walk invocations, writes, clones, construction and readiness switch arms. Restrict the bare factory exception to the recorded-reason fallback. Diagnose syntax errors separately. Test syntax-valid mutations by violation and line alongside valid same-line and comment controls.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Accepted the 1bf1263 review for failed-result initializers and direct ref/out writes after checking the two guards. The audit checks syntax in EngineRuntime.cs. Behavioral tests own readiness-arm mapping and runtime defaults. Independent review of the 183ab7d fixture-only repair was dispatched November 19.
<!-- SECTION:NOTES:END -->
