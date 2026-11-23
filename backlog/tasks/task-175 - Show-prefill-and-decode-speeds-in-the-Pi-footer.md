---
id: TASK-175
title: Show prefill and decode speeds in the Pi footer
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T12:35:00Z'
updated_date: '2026-11-23T22:01:00Z'
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
- [x] #1 The footer labels prefill and decode separately in tokens per second and updates each as measurements become available.
- [x] #2 Prefill excludes queue and model-load time. Decode uses generated tokens and generation time.
- [x] #3 An unavailable rate displays a dash. A new request clears old rates, and overlapping sessions only see their own measurements.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Merged in [PR 82](https://github.com/ArborealML/Inference-Router/pull/82). Real Pi RPC checks verified fresh rates per request and isolated overlapping sessions. Native progress measures uncached prompt tokens during processing. Decode uses native timings when available and labels estimates. Late prompt progress preserves the response phase.
<!-- SECTION:NOTES:END -->
