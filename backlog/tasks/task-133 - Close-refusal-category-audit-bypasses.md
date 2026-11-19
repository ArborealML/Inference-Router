---
id: TASK-133
title: Close refusal-category audit bypasses
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T18:41:00Z'
labels: []
dependencies: []
priority: high
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Shutdown owns the fixture repair for [PR #10](https://github.com/ArborealML/Inference-Router/pull/10). On head 1bf1263 the hosted Ubuntu Release test step failed three of 1,465 cases against the merge tree. Two negative mutations and one benign mutation could not find the obsolete RecordedExit text, so none reached the audit. Current EngineRuntime has a standalone return and a conditional expression ending in null. Update the fixture on current main while preserving the expected violation and benign control. Run RefusalCategoryTests on the assembled head, then verify hosted CI. [Failed job](https://github.com/ArborealML/Inference-Router/actions/runs/44589611159/job/135791730275).
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
The f382534 head passed 38 focused cases. Independent review found a failed-result initializer bypass and direct ref/out access to FailureReason. The 1bf1263 correction covers both and passed 41 focused tests on its original base. Independent correction review was dispatched November 19. The hosted failure is integration-specific. The audit is syntax-only in EngineRuntime.cs; behavioral tests own readiness-arm mapping and the existing health_timeout and switch_error defaults.
<!-- SECTION:NOTES:END -->
