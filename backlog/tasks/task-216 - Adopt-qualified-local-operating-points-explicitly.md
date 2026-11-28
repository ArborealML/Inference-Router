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
  - TASK-195
priority: high
ordinal: 216000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement explicit operator-reviewed adoption mechanics from exact recipe/evidence identities and safe capacity, with deterministic fixture records first. Separate physical launch P, window W, admission target C and occupancy c. Actual runtime use requires real qualified evidence and explicit adoption.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Adoption retains multiple passing operating points and comparable occupancy samples, stable bound and measured optimum; untested points never become qualified.
- [ ] #2 Host verifies weights, quantization, backend, tokenizer/template, KV/resources and hardware identity before publishing qualified capacity.
- [ ] #3 Ordinary restart reuses unchanged evidence with fresh live capability proof; material drift invalidates affected capacity and performance evidence.
- [ ] #4 Operator adoption is explicit, does not overwrite active settings automatically, and preserves raw records and prior defaults.
- [ ] #5 SPEC §§5, 7.6, 11, 14 and focused tests cover malformed/changed evidence, restart, reload and conservative fallback.
- [ ] #6 Synthetic records qualify validation/adoption mechanics but cannot enable production capacity; runtime activation needs real authorized host evidence and explicit operator adoption.
- [ ] #7 Adoption records exact compatible evidence hashes and enabled workload modes separately for admission-only and physical changes; synthetic or homogeneous evidence cannot authorize unqualified mixed/staggered operation.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse versioned evidence and exact recipe identity; test with fixture records without waiting for TASK-154 real trials. Operator-owned manifest records approved hashes and qualified points. No general artifact registry, database or implicit adoption. Production selection is disabled for unverified recipes or workload modes; actual mixed/staggered evidence comes from TASK-154.
<!-- SECTION:PLAN:END -->
