---
id: TASK-195
title: >-
  Decide what a named serving tier means when two tiers share a window and lane
  count
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-26T07:34:00Z'
updated_date: '2026-11-26T09:22:00Z'
labels:
  - config
  - scheduling
dependencies:
  - TASK-189
priority: medium
ordinal: 187000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decision needed, and the containment work that made capacity the scheduling authority has now merged, so the question is live rather than theoretical. Two declared tiers of one model can request the same context window and the same lane count while launching different things — a different quantization, a different key-value cache type, or a different memory budget. Measured capacity cannot tell them apart, and the inferred profile label is deliberately null whenever more than one declared tier matches the measured pair, so a request naming one tier can be served by an instance launched for the other. Choose one: refuse the configuration at startup, start with a warning and last-declared-wins, or declare such tiers interchangeable and document it. This unblocks the ambiguous-label pin and settles what a tier name means to an operator.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC says whether two tiers of one model with equal declared window and lane count but different launch configuration are refused, warned, or interchangeable, and one test produces that outcome.
- [ ] #2 The chosen answer matches the accepted rule that the profile name is a display inference and the measured window is the scheduling authority, or the ruling is revised in a decision entry.
<!-- AC:END -->
