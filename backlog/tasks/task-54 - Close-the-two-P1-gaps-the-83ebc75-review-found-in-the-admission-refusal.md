---
id: TASK-54
title: Close the two P1 gaps the 83ebc75 review found in the admission refusal
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T19:47:00Z'
labels:
  - scheduling
  - wire
  - concurrency
dependencies: []
priority: high
ordinal: 54000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The commit review of 83ebc75 (the fast-refuse work) returned REVISE. Both P1s are being re-verified in the code before anything changes. (1) Tickets admitted while an engine's capability was **unknown** stay queued once absence becomes **known**: nothing revisits them, so they hold queue positions indefinitely and a later request for that model can be answered queue_overflow instead of model_unavailable. (2) The multi-engine capability scan is not a coherent snapshot — a probe publication landing between two engine reads can produce a 503 for a model some engine was reporting throughout. P2: the scheduler view captures the loaded list and then re-reads it for the eligibility bit, so the two can disagree after a probe update. P3: SPEC 7 overstates what the feed carries (nothing emits those wait reasons) and claims nothing accumulates in the queue during the unknown window, which is false. Plan review first: this crosses concurrency and the wire. The uncapped-wait task is adjacent to the remedy for P1-1, not independent of it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 queued tickets are refused model_unavailable once every engine is known and none can serve, pinned end to end with two queued tickets and no max_queue_time
- [ ] #2 the refusal is evaluated against one coherent capability snapshot, pinned by a deterministic two-engine interleaving
- [ ] #3 the scheduler view derives the loaded list and the eligibility bit from that one snapshot
- [ ] #4 SPEC 7 and 6.1 say what the code does, including what the feed does not carry
<!-- AC:END -->
