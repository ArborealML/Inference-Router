---
id: TASK-56
title: Emit one event per refusal on the queue-cap overflow path
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - wire
  - scheduling
milestone: m-1
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
- [ ] #4 Events.Replay(0) after a queued ticket's timeout (max_queue_time) shows exactly one queue.rejected with reason queue_timeout and no request.completed
- [ ] #5 Events.Replay(0) after a client disconnect shows exactly one request.canceled and no request.completed
- [ ] #6 Events.Replay(0) after a successful switch shows one engine.switch_completed and no engine.switch_failed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Measured at 21:5xZ by the coding session, and the reason this grew from one path to four.** The cancellation path was already right and I had wrongly flagged it: TryCancel (RouterCore.cs:206) routes through ModelQueue.cs:113-116, which emits request.canceled and no completed. What was missing is the timeout path: the max_queue_time handler (RouterCore.cs:419-445) emits queue.rejected (queue_timeout) and then request.completed. And a lifecycle event carries no outcome field, so the live view renders switch_failed and switch_completed identically (BlazorClientTests.cs:345-357).

**One-event rule, stated per surface.** A request emits one terminal event on the request surface (queue.rejected | request.canceled | request.completed | request.failed) and one terminal event on the lifecycle surface (engine.switch_failed | engine.switch_completed); an in-flight request emits no lifecycle event at all. The wire shape stays OpenAI-compatible because request.completed is what an SSE stream ends with, so a canceled or refused request never reaches the client with a completion.
<!-- SECTION:NOTES:END -->
