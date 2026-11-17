---
id: TASK-55
title: Close the probe and switch publication races the 20a06fa review found
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-17T21:58:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Four external reviews of 20a06fa returned REVISE; each finding was verified in the code, none a gap.
One invariant covers them: **a capability snapshot is either current-and-verified or unknown** — today it can be
current-and-assumed. Marker and switch state are ordered wrongly at both ends: EngineRuntime.cs:457 installs the switch state
and :458 publishes the marker, so a dispatch pass between them sees an engine claimed for a model load but not marked switching
and can send a request into the engine about to be stopped; EndSwitch (:551-558) has the mirror order without _switchLock.
Swapping those two statements is **not** the fix — the scheduler reads Switching from the snapshot marker (RouterCore.cs:462),
so clearing the marker first makes a mid-switch engine look idle. The shape is a short await-free **transition gate** over both
transitions: on start, publish marker and unknown together, then install the switch state; on end, clear the marker only while
it still names that switch, new starts excluded; readiness resolution stays outside the gate; lock order is switch-lock then
gate (:472). A probe begun during a mutation also publishes a stale-but-healthy claim, the failure publish at :228 writes a
list captured at call time, and every poll bumps the version, so the scan never settles and the shipped refusal rarely fires.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a probe publication landing during a switch is gated: the switch's list is never replaced by an older one
- [ ] #2 the start publishes marker and unknown together, then installs the switch state; a barrier test shows no dispatch pass enters in between
- [ ] #3 the end clears the marker only while it still names that switch, under a gate that excludes new starts
- [ ] #4 a probe begun during a lifecycle mutation publishes neither Healthy=true nor a model list; restoration comes from the switch's proof or a later probe
- [ ] #5 two identical successive probes leave the capability version unchanged, so the scan settles
- [ ] #6 every uncertain switch exit (failed stop or launch, readiness timeout, unverified completion, failed refresh) publishes health-only, never a captured list
- [ ] #7 a switch whose health proof succeeded makes the engine eligible without another poll, list and health in one publication
- [ ] #8 after a switch commits, a waiting queued request is considered again without a poll
- [ ] #9 a readiness answer requires a healthy, marker-free capability holding the target with a fitting window
- [ ] #10 SPEC 5.5 and a revision-log line state the gate, the publication rule, the unknown-on-uncertain-exit posture, and the accepted gaps
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What the plan reviews changed.** The first falsified the marker fix this task first carried; the second found the start-side
ordering is the live half, that a successful switch never restores health (:603-614 publishes the list while only the periodic
poll writes Healthy=true), that the wake fires before the commit while the marker clear emits nothing (RouterCore.cs:66-71) —
one poll of latency, not a stall — and that IsReadyFor (:414-418) reads list and gauge alone.
**Accepted gaps, for SPEC 5.5 not code:** the fence orders publications, not reality — an engine restarted outside the router
with no visible discontinuity keeps a stale-but-healthy snapshot until the next poll. And the health loop discards the
Expect-DSL result (:221-225): **Healthy means the /models probe answered**, not that the readiness rule passed, which gates
switch readiness only. No open task says otherwise.

**Order:** gate, fence, health-only failure publish, unknown-on-uncertain-exit, then quiet-poll version stability. TASK-57,
TASK-56, TASK-54 follow.<!-- SECTION:NOTES:END -->
