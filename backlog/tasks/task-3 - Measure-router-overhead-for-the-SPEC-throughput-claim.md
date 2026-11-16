---
id: TASK-3
title: Measure router overhead for the SPEC throughput claim
status: To Do
assignee: []
created_date: '2026-11-16T20:40:00Z'
labels: []
dependencies: []
ordinal: 3000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The spec claims request-path overhead is negligible but it is unmeasured. Benchmark router-fronted requests against direct engine calls at several concurrency levels and record the numbers the claim needs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A repeatable benchmark harness lives in the repo with documented run instructions
- [ ] #2 Measured p50/p95/p99 overhead at 1, 4, and 16 concurrent streams
- [ ] #3 SPEC section states the measured numbers instead of an unmeasured claim
<!-- AC:END -->
