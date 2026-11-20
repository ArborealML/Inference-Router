---
id: TASK-56
title: Emit one event per refusal on the queue-cap overflow path
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T20:45:00Z'
labels:
  - wire
  - scheduling
milestone: m-3
dependencies: []
priority: medium
ordinal: 56000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Frames is auditing queue overflow, timeout and cancellation against current admission code and landed feed tests on ebcca83. The historical overflow path at 20a06fa could report both request.enqueued and queue.rejected. Identify which criteria already have evidence and the smallest remaining gap before proposing a change.
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
