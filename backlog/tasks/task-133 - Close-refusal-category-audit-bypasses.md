---
id: TASK-133
title: Close refusal-category audit bypasses
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-19T06:27:00Z'
updated_date: '2026-11-19T06:47:00Z'
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
Plan review verified the audit on 5e345cc. Syntax checks apply within one file and deliberately reject Detail or Outcome clone assignments there. Preserve the existing behavioral checks, health_timeout readiness fallback and switch_error empty-record fallback.
<!-- SECTION:NOTES:END -->
