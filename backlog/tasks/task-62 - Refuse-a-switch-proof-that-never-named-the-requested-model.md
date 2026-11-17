---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-17T23:29:00Z'
labels:
  - concurrency
  - wire
dependencies:
  - TASK-66
priority: high
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router can vouch for a model nothing ever reported: on the wait_health=false path a successful /models fetch is published as a
proof and the switch ends ready whether or not the reported list names the requested model, with no membership check on that path
(EngineRuntime.cs:796-798, :818-820). That half is a plain bug — wait_health=false skips the health.expect rule, never the check
that the engine says it holds this model — and fixing it changes no operator contract. **The other half found in the same code is
a policy call, and it lives in [TASK-66](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-66%20-%20Decision-should-a-failed-health-proof-stop-serving-a-wait_healthfalse-model.md):**
today a *failed* refresh still serves the request and marks the capability unknown, and the provider interface calls that an
operator promise made in config rather than a fallback the router may choose (IEngineLifecycleProvider.cs:61-63), pinned at
CapabilityPublicationTests.cs:399-403. A test also pins the wrong posture today (:387-413) and moves with the fix.
<!-- SECTION:DESCRIPTION:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**A review claim rejected, so nobody builds it.** The review warned of a failure latch or backoff for repeated relaunches after an
unverified switch. That hazard is not in this code: once PublishUnknown lands, the snapshot reads Healthy=false, and
PrefixAwareScheduler.cs:71 skips unhealthy engines outright, so nothing relaunches in a loop.

**Landing order.** Set 1 (no operator-visible change): this task's bug half, TASK-61, and TASK-63's fenced negative publish. Set 2
(policy): TASK-62's remaining half behind TASK-66, and TASK-63's gauge work, which needs a plan decision first.
<!-- SECTION:NOTES:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a refresh whose reported list omits the target ends switch_failed with an actionable reason, Healthy=false, and no Ready
- [ ] #2 if TASK-66 changes the posture, the switch_failed wording on the wire (RouterCore.cs:542-551) and the feed event change in the same commit
- [ ] #3 the test at CapabilityPublicationTests.cs:387-413 is rewritten to the corrected posture, and the exec's own success still appears on the feed
<!-- AC:END -->
