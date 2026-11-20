---
id: TASK-56
title: Emit one event per refusal on the queue-cap overflow path
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-frames'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T20:48:00Z'
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
The audit at ebcca83 found that [reload admission](https://github.com/ArborealML/Inference-Router/pull/43) fixed the historical duplicate enqueue after overflow. The remaining plan covers the feed contract and two exact ticket replay assertions: queue overflow and a real HTTP disconnect.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Events.Replay(0) after a queue-cap rejection shows exactly one queue.rejected with reason queue_overflow and no request.enqueued
- [x] #2 the model_unavailable admission refusal still shows one queue.rejected and no request.enqueued, pinned in the same test run
- [ ] #3 SPEC 6.1 states one event per terminal admission outcome
- [x] #4 A queued ticket reaching its deadline fails with deadline_exceeded and emits exactly one request.failed terminal event.
- [ ] #5 Events.Replay(0) after a client disconnect shows exactly one request.cancelled and no request.completed
- [x] #6 Events.Replay(0) after a successful switch shows one engine.switch_completed and no engine.switch_failed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
EndToEndTests covers the full ticket replay and reason for an unavailable model. TicketLifetimeTests covers deadline_exceeded; ConfigReloadRaceTests and ExactTicketAdmissionTests check one failure after acceptance. HostLifecycleCoordinatorTests counts one switch completion across both terminal kinds. The [landed Release suite](https://github.com/ArborealML/Inference-Router/actions/runs/44813198939) passed 1,769 tests with zero skips. The existing overflow fixture checks event ordering; strengthen it to compare the complete ticket replay and reason. The disconnect fixture checks the first terminal event; count every terminal event for that ticket after settlement. SPEC §6.1 should distinguish pre-queue refusal from the terminal event for an accepted ticket, with consistent wording in §§5.5 and 10.
<!-- SECTION:NOTES:END -->
