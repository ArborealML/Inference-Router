---
id: TASK-163
title: Expose the DeepSeek one-million-token selector profile in AIR
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T03:06:00Z'
updated_date: '2026-11-22T03:32:00Z'
labels: []
dependencies: []
priority: high
ordinal: 158000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Port the existing single-slot max profile as an explicit Pi context choice while keeping the normal default at 256k.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The optional 1m profile selects 1048576 tokens and one slot; the default stays 262144 tokens.
- [x] #2 Verify configured discovery and independently measure the served window and slots on a completed live request.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Port the existing single-slot max profile as an explicit Pi context choice while keeping the normal default at 256k.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The applied ten-engine catalog offers the optional single-slot DeepSeek IQ2 1m tier and still selects 256k by default. An ordinary Pi 1m request streamed READY; the backend reported its configured model loaded with 1048576 tokens and one slot. This was a short reply, not a full-window workload benchmark. [Configured profile contract](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md).
<!-- SECTION:NOTES:END -->
