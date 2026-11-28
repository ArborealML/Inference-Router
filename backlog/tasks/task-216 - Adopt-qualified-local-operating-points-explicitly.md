---
id: TASK-216
title: "Adopt qualified local operating points explicitly"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-14
dependencies:
  - TASK-171
  - TASK-154
priority: high
ordinal: 216000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Publish a small operator-reviewed adoption manifest of exact recipe/evidence identities and safe capacity; AIR consumes only adopted qualified points. Separate physical launch P, nominal context W, admission target C and measured occupied concurrency c.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Adoption retains multiple passing operating points and comparable occupancy samples, stable bound and measured optimum; untested points never become qualified.
- [ ] #2 Host verifies weights, quantization, backend, tokenizer/template, KV/resources and hardware identity before publishing qualified capacity.
- [ ] #3 Ordinary restart reuses unchanged evidence with fresh live capability proof; material drift invalidates affected capacity and performance evidence.
- [ ] #4 Operator adoption is explicit, does not overwrite active settings automatically, and preserves raw records and prior defaults.
- [ ] #5 SPEC §§5, 7.6, 11, 14 and focused tests cover malformed/changed evidence, restart, reload and conservative fallback.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse existing evidence storage and exact recipe identifiers. A small versioned manifest suffices; no new database, forecast service or implicit adoption from a comparator result.
<!-- SECTION:PLAN:END -->
