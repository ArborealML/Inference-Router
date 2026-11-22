---
id: TASK-161
title: Make air command output readable by default and support --json
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:37:00Z'
labels: []
dependencies: []
priority: high
ordinal: 156000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Give people readable status, model, queue, switch and event output while retaining machine-readable output on request.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ordinary diagnostic commands render useful readable output by default.
- [ ] #2 The global --json flag preserves structured data and existing protocol or artifact commands remain usable.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Give people readable status, model, queue, switch and event output while retaining machine-readable output on request.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Readable air status and machine-readable air --json status both verified against the installed ten-engine catalog. Focused Release tests passed, including refused event streams. [Integration](https://github.com/ArborealML/Inference-Router/pull/78).
<!-- SECTION:NOTES:END -->
