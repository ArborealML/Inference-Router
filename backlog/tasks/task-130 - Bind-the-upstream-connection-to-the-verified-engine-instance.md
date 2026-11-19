---
id: TASK-130
title: Bind the upstream connection to the verified engine instance
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T22:19:00Z'
updated_date: '2026-11-19T07:33:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 128000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI and review of the strengthened fixture wake the offline transport landing.

Bind the first upstream write to the exact verified instance and grant. Dispatch integration consumes the transport after landing. Counted admission and drain remain separate.
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
At f757e3a all three first-write Release cases passed. That fixture uses a manual half-close to check buffered bytes. [PR #11](https://github.com/ArborealML/Inference-Router/pull/11) at e629c96 retains the vacancy revision and restores the exact transport source and both fixture files from 73e8cbe (27/27 Release, linux-x64 Native AOT with zero trim warnings). The issuer explicitly revokes its withheld grant and observes peer EOF. A distinct grant wrapping the same stream refuses. Production request body and authentication binding, durable issuance and physical retirement remain open.
<!-- SECTION:NOTES:END -->
