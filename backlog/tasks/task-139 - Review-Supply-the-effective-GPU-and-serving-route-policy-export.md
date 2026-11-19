---
id: TASK-139
title: 'Review: Supply the effective GPU and serving-route policy export'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-19T18:53:00Z'
updated_date: '2026-11-19T19:01:00Z'
labels: []
dependencies: []
priority: high
ordinal: 137000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner returned the privileged export on November 19 at 19:00 UTC. The coordinator verified the resident identity, two effective device programs, GPU nodes, sampled listeners and daemon socket modes. Both device programs allow resident reads and writes to both GPU devices. Independent exclusion, writer and observer enrollment, and journal freshness remain unverified. Raw evidence is retained privately. [Host enrollment](./task-110%20-%20Coordinate-model-engines-that-share-one-host-with-exclusive-residency.md) owns interpretation and implementation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Return the redacted effective device and serving-route readback, marking unavailable or unverified observations explicitly.
- [x] #2 Identify any configured writer, observer and journal anchor with its snapshot boundary, or report their individual dispositions.
<!-- AC:END -->
