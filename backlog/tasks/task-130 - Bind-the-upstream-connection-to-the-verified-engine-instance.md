---
id: TASK-130
title: Bind the upstream connection to the verified engine instance
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T22:19:00Z'
updated_date: '2026-11-19T07:27:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 128000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Prepare the reviewed offline instance transport for landing. Bind the first upstream write to the exact verified instance and grant. TASK-118 integrates profile resolution and dispatch. TASK-25 and TASK-64 own counted admission and drain.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A controlled original instance receives the expected bytes through its verified route. A replacement bound to the same public address after verification receives zero bytes under the original proof.
- [ ] #2 Retiring an instance invalidates its route and connection pool. A request with retired proof cannot write through a pooled connection, redirect, retry or endpoint fallback.
- [ ] #3 The transport binds the immutable instance and launch epoch. Mismatching either rejects the write even when the model and public address match.
- [ ] #4 Race two independent transport callers against the same grant. The issuer authorizes at most one submission. Substituting another grant at first write sends zero bytes under the original authorization.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one authenticated application stream per invocation. Freeze the enrollment, process, route incarnation and launch epoch. Serialize HTTP/1.1 above the authenticated stream. Keep the exchange and physical permit owned through terminal cleanup. Exercise redirects, retries, stale grants, first-write races and late responses against independently observable original and replacement sinks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Exact-grant first-write validation at 0059208 passed 26 focused Release cases and server Native AOT with zero trim warnings. The substitute grant reaches the write check and yields zero bytes; the matching grant sends the exact body. Two transport objects share one fake issuer and cannot reuse an invocation. The same-container restart control passed. Review accepted the shared-issuer behavior. Add a distinct grant wrapping the same stream and invocation to isolate object identity. The issuer owns cleanup of the withheld original. The half-close proves absence of buffered bytes only. Prepare the offline landing on current main. Reserve SPEC v0.12.77. Production body and authentication binding, durable issuance and physical retirement remain open.
<!-- SECTION:NOTES:END -->
