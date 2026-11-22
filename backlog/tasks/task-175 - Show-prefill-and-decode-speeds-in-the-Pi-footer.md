---
id: TASK-175
title: Show prefill and decode speeds in the Pi footer
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T12:35:00Z'
labels: []
milestone: m-7
dependencies: []
references:
  - tools/pi-air/index.mjs
ordinal: 146000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Replace the thread ID in the Pi bottom bar with prompt-processing and generation speeds for the active AIR request.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The footer labels prefill and decode separately in tokens per second and updates each as measurements become available.
- [ ] #2 Prefill excludes queue and model-load time. Decode uses generated tokens and generation time.
- [ ] #3 An unavailable rate displays a dash. A new request clears old rates, and overlapping sessions only see their own measurements.
<!-- AC:END -->
