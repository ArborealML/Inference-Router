---
id: TASK-130
title: Bind the upstream connection to the verified engine instance
status: Preparing
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T22:19:00Z'
updated_date: '2026-11-19T02:27:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 128000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement the transport used between host readiness verification and the first upstream write. The [internal host enrollment seam](https://github.com/ArborealML/Inference-Router/commit/ad1790e) supports offline work now. TASK-118 integrates this transport with profile resolution and dispatch. TASK-25 and TASK-64 own counted admission and drain.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A controlled original instance receives the expected bytes through its verified route. A replacement bound to the same public address after verification receives zero bytes under the original proof.
- [ ] #2 Retiring an instance invalidates its route and connection pool. A request with retired proof cannot write through a pooled connection, redirect, retry or endpoint fallback.
- [ ] #3 The transport binds the immutable instance and launch epoch. Mismatching either rejects the write even when the model and public address match.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Transport plan review requires revision. Freeze the enrollment, instance, epoch and route incarnation for each one-use exchange. Specify which layer authenticates TLS and where the HTTP serializer writes its first byte. Assign the stream, response and physical permit one cleanup owner through late completion. Use separate original and replacement sinks to check retirement, redirects and replay before runtime integration.
<!-- SECTION:PLAN:END -->
