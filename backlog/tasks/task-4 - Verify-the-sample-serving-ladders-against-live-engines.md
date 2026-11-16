---
id: TASK-4
title: Verify the sample serving ladders against live engines
status: To Do
assignee: []
created_date: '2026-11-16T20:40:00Z'
labels: []
dependencies: []
ordinal: 4000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The shipped sample configs declare context/slot ladders that were written from expectation, not measurement. Probe a live engine per backend and correct the samples, or annotate which values are unverified.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Each ladder entry in samples/router.toml has a measured context and slot count or is marked unverified
<!-- AC:END -->
