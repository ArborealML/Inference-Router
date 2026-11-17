---
id: TASK-56
title: Emit one event per refusal on the queue-cap overflow path
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
labels:
  - wire
  - scheduling
dependencies: []
priority: medium
ordinal: 56000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A request refused by the per-model queue cap reports that it queued. Checked in the tree at 20a06fa: RouterCore.cs appends request.enqueued after the enqueue attempt with no regard for the answer (:175-184 region), while the rejection callback fires inside ModelQueue.TryEnqueue (Queue/ModelQueue.cs:36-48), so an overflow emits both request.enqueued and queue.rejected for a ticket that never entered a queue. Pre-existing, not introduced by the admission-refusal work, which got its own path right: that one answers 503 model_unavailable as exactly one queue.rejected with no request.enqueued. This is a wire-contract defect on the same surface — a feed reader that reconciles enqueues against completions and refusals goes wrong on every overflow, and the event stream is what the live view and the Pi extension read. Smallest honest shape: append the enqueue event only when the ticket was accepted.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Events.Replay(0) after a queue-cap rejection shows exactly one queue.rejected with reason queue_overflow and no request.enqueued
- [ ] #2 the model_unavailable admission refusal still shows one queue.rejected and no request.enqueued, pinned in the same test run
- [ ] #3 SPEC 6.1 states one event per terminal admission outcome
<!-- AC:END -->
