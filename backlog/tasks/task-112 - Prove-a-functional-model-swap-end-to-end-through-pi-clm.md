---
id: TASK-112
title: Prove a functional model swap end to end through pi-clm
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-22T01:15:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-125
  - TASK-110
  - TASK-108
  - TASK-114
  - TASK-25
  - TASK-64
  - TASK-118
  - TASK-143
ordinal: 110000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two Pi clients selected configured models through ordinary queue admission. The original queued request drove the live switch and streamed its reply. The generic implementation landed in the [merged Pi integration](https://github.com/ArborealML/Inference-Router/pull/75).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Complete a pi-clm turn on model A, request model B through the router, and complete a streamed pi-clm turn on B.
- [x] #2 Correlate the switch with observed backend residency before and after and successful client completion. Record redacted evidence, router version and config revision.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse the recorded live run and correlate both client completions with Docker and backend residency.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Qwen streamed READY and finished. With client retries disabled, the original queued DeepSeek ticket drove the Docker transition and streamed SECOND_READY. Docker showed the old container stopped. Authenticated backend readback reported DeepSeek at 32,768 tokens and one slot, and the native worker occupied both GPUs. See the [published live verification](../../docs/local-model-switching.md#verification) and [main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44967781268).
<!-- SECTION:NOTES:END -->
