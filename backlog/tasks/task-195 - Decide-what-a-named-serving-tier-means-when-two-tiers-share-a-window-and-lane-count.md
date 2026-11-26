---
id: TASK-195
title: >-
  Decide what a named serving tier means when two tiers share a window and lane
  count
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T07:34:00Z'
updated_date: '2026-11-26T07:35:00Z'
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
Wake: integration of pull request #85, which makes measured capacity the scheduling authority. Two declared tiers of one model can request the same context window and the same lane count while launching different things — a different quantization, a different key-value cache type, or a different memory budget. Measured capacity cannot tell them apart, and the inferred profile label is deliberately null whenever more than one declared tier matches the measured pair, so a request naming one tier can be served by an instance launched for the other. Decide whether such a configuration is refused at startup, reported as a warning with last-declared-wins, or treated as interchangeable and documented, then state the answer in SPEC and pin it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC says whether two tiers of one model with equal declared window and lane count but different launch configuration are refused, warned, or interchangeable, and one test produces that outcome.
- [ ] #2 The chosen answer matches the accepted rule that the profile name is a display inference and the measured window is the scheduling authority, or the ruling is revised in a decision entry.
<!-- AC:END -->
