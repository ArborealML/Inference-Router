---
id: TASK-130
title: Bind the upstream connection to the verified engine instance
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T22:19:00Z'
updated_date: '2026-11-19T07:37:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 128000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The internal single-host transport binds the first application write to the verified invocation, stream and grant. The offline issuer fixtures cover rebinding, redirects, restart and retirement. TASK-118 owns production dispatch integration and frozen body and authentication policy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A controlled original instance receives the expected bytes through its verified route. A replacement bound to the same public address after verification receives zero bytes under the original proof.
- [x] #2 Retiring an instance invalidates its route and connection pool. A request with retired proof cannot write through a pooled connection, redirect, retry or endpoint fallback.
- [x] #3 The transport binds the immutable instance and launch epoch. Mismatching either rejects the write even when the model and public address match.
- [x] #4 Race two independent transport callers against the same grant. The issuer authorizes at most one submission. Substituting another grant at first write sends zero bytes under the original authorization.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one authenticated application stream per invocation. Freeze the enrollment, process, route incarnation and launch epoch. Serialize HTTP/1.1 above the authenticated stream. Keep the exchange and physical permit owned through terminal cleanup. Exercise redirects, retries, stale grants, first-write races and late responses against independently observable original and replacement sinks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #11](https://github.com/ArborealML/Inference-Router/pull/11) merged at 7ec1c20. Its head e629c96 passed [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44480507043). The transport source and both fixture files match 73e8cbe, which passed 27/27 focused Release tests and linux-x64 server Native AOT with zero trim warnings. Review accepted the distinct-object same-stream refusal, exact successful grant and explicit issuer revocation with peer EOF. The same-stream negative does not establish when EOF first became observable. Checks cover a shared fake issuer. Production issuance, authenticated dispatch and physical retirement remain separate work.
<!-- SECTION:NOTES:END -->
