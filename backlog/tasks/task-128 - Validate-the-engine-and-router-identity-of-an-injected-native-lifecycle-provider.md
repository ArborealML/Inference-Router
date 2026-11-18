---
id: TASK-128
title: >-
  Validate the engine and router identity of an injected native lifecycle
  provider
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T21:07:00Z'
updated_date: '2026-11-18T22:27:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 126000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The runtime checks the injected provider kind. The concrete Docker provider independently captures its engine name and router configuration for ownership queries. A same-kind provider bound to another engine can pass construction. The default factory supplies the matching name.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inject an actual Docker provider configured for a different engine, router identity or daemon scope. Construction or binding refuses it before any provider HTTP, ownership query, stop or create.
- [x] #2 With matching binding, preserve the supported positive path. A forwarding-only engine issues zero lifecycle calls even when a provider is supplied.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review the concrete injection and default factory call sites. Bind the native operation scope once and validate it before runtime use. Preserve existing supported fake and external behavior; exercise real provider binding with an in-memory handler.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Concrete-provider design review dispatched. The planned fixture uses the actual native provider with an in-memory daemon handler and asserts zero I/O on binding rejection.

The [integrated startup check](https://github.com/ArborealML/Inference-Router/commit/2ff6701) passed 178 focused Release cases and server Native AOT. On the unfixed parent all 11 mismatch and invalid-scope controls failed because construction accepted the configuration. The corrected fixtures require field-specific rejection and zero daemon calls. Matching and forwarding-only controls passed. External implementation and test reviews were dispatched.
<!-- SECTION:NOTES:END -->
