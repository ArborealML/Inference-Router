---
id: TASK-133
title: Close refusal-category audit bypasses
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T07:13:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 131000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Audit individual failure expressions in EngineRuntime so record clones, same-line calls and compound writes cannot hide unclassified caller-facing reasons.
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
Review of c0adf0d and readback at [PR #10](https://github.com/ArborealML/Inference-Router/pull/10) head f382534 confirmed two remaining syntax bypasses. Inspect failed-result object initializers as well as constructor arguments, and explicit ref/out access to FailureReason as well as assignments. Add targeted valid mutations. The 38 focused tests and hosted CI passed on f382534. Syntax findings are confined to EngineRuntime.cs. Exact readiness-arm mapping remains covered by behavioral tests. Preserve health_timeout and switch_error defaults.
<!-- SECTION:NOTES:END -->
