---
id: TASK-195
title: "Bind exact recipe identity when context and lane counts coincide"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T07:34:00Z'
updated_date: '2026-11-27'
labels:
  - config
  - scheduling
milestone: m-16
dependencies:
  - TASK-189
priority: medium
ordinal: 187000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two recipes with equal window and lane count can launch different weights, quantization, KV or resource settings. Proposed resolution: distinct exact identity; refuse ambiguous exact launch until bound. Numeric suitability may use a qualified resident independent of display label.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Same-window/same-lane distinct recipes keep separate identities and effective settings; no last-declared-wins or assumed interchangeability.
- [ ] #2 Ambiguous exact launch refuses before mutation; bound launch verifies actual recipe and readiness.
- [ ] #3 Context-only request can use a fitting qualified resident without a bundled label or unproved capacity.
- [ ] #4 SPEC §§5 and 7.6 and fixtures distinguish suitability, display inference, exact identity and conformance.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Coordinate TASK-197/118. Preserve null ambiguous display; resolve exact recipe before committing mutation. Review contract before implementing.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Former owner question addressed by revision 4 recommendation, subject to PR ratification; not marked Done.
<!-- SECTION:NOTES:END -->
