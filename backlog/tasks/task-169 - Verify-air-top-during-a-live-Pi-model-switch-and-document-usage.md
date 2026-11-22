---
id: TASK-169
title: Verify air top during a live Pi model switch and document usage
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:10:00Z'
updated_date: '2026-11-22T04:48:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-168
references:
  - README.md
ordinal: 164000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Exercise the installed monitor with ordinary Pi traffic and publish usage and observed behavior.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 While one request finishes on its resident model, the original queued request for another configured model drives the switch and streams without an admin switch.
- [x] #2 The monitor follows that ticket through dispatch and the reported residency change agrees with an independent backend readback.
- [x] #3 Opening and quitting the monitor leave active requests intact. README documents the command, keys and stale-data indicators.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Exercise the installed binary with two ordinary Pi clients in an idle window. Capture the first request completing, the original queued request dispatching and streaming, the monitor view and independent backend residency. Document the verified entry point and controls.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Installed air top followed the original queued Pi ticket through a physical switch. DeepSeek completed with 474 text deltas. Qwen streamed on Strata and finished with 480 deltas after the monitor quit. Direct model and slot readbacks reported 262144 context tokens and one slot on each backend. The original container stopped and the new native process used both GPUs. The SSH session restored its terminal. README documents the command and controls. [Implementation and verification](https://github.com/ArborealML/Inference-Router/pull/79).
<!-- SECTION:NOTES:END -->
