---
id: TASK-128
title: >-
  Validate the engine and router identity of an injected native lifecycle
  provider
status: Preparing
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T21:07:00Z'
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
- [ ] #1 Inject an actual Docker provider configured for a different engine, router identity or daemon scope. Construction or binding refuses it before any provider HTTP, ownership query, stop or create.
- [ ] #2 With matching binding, preserve the supported positive path. A forwarding-only engine issues zero lifecycle calls even when a provider is supplied.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review the concrete injection and default factory call sites. Bind the native operation scope once and validate it before runtime use. Preserve existing supported fake and external behavior; exercise real provider binding with an in-memory handler.
<!-- SECTION:PLAN:END -->
