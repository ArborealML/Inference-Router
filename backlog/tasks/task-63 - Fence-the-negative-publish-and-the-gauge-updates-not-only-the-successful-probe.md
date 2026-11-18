---
id: TASK-63
title: >-
  Fence the negative publish and the gauge updates, not only the successful
  probe
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - concurrency
milestone: m-2
dependencies: []
priority: medium
ordinal: 63000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
fd5e0cd fenced the successful probe and left the other two publication paths open. The health loop's failure path calls an unfenced PublishUnknown (EngineRuntime.cs:328-333, 231-239), so a poll that failed can overwrite fresher evidence and mark a working engine unknown until the next interval — the safe direction, but still a wrong answer that costs a model its engine. The context-window and slot gauges from /props are applied with no epoch at all (:413-442, 528-545, and PrefixAwareScheduler.cs:76-92), so a stale window answer can mislead readiness and the escalation ladder, which is the more dangerous of the two. Both are generation-fence gaps rather than new mechanisms.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the health loop's failure path calls the fenced negative publish that already exists (:172-185) instead of the unfenced one (:328-333), pinned by a test
- [x] #2 SPEC 5.5's quiet-poll wording says what the comparison actually does rather than describing the published reference
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
First of the set-1 commits. Hoist the version read out of the `try` at :317 or the catch cannot use it. Extract the `!healthy` branch of `PublishProbeResult` (:177-186) as `PublishProbeFailure(long expectedVersion)` and call it from the health-loop catch (:328-334). One version across the round trip is not enough: the loop makes two awaited observations (model fetch, then the serving-state probe), and if the first one publishes a recovery the second's failure is fenced away by the loop's own newer version, silently keeping a working flag the old code would have cleared. Either fence each observation against its own version, or decide out loud that a serving-state probe failure no longer touches health, and say which in SPEC. `PublishUnknown()` (:231-241) **stays unfenced** at every switch exit (:746, :783, :795, :803, :817) — the switch is the fresher publisher by construction — and that reasoning goes in its doc comment. AC #3's one §5.5 sentence lands here. Pinning needs a `ProbeHandler` seam that can fail a *held* probe (today `ModelsStatus` is applied before the hold, so a held probe can only answer 200), plus a case where the fetch recovers health and the state probe then fails.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Landed at [0e016e5](https://github.com/ArborealML/Inference-Router/commit/0e016e5) (v0.12.29): the fenced negative publish and the §5.5 quiet-poll wording.** The two-observation question the plan review raised was settled in the "say it out loud" direction, and the row's own reading of the code backs it: `ProbeStateAsync` swallows its own failures (:408-430), so the serving-state probe cannot reach the health loop's catch at all; the `/models` fetch is the only health-moving observation, in either direction, and SPEC §5.5 now says so with a test pinning it. The same-tick case — the fetch answers, publishes a recovery, and a later line throws — is fenced away by this tick's own publication, and the catch says that in words.

**AC #2 left this row and became TASK-68.** The plan review showed the serving gauges can authorise a wrong dispatch, not merely a late refusal, which is a larger deliverable than fencing a publication path.
<!-- SECTION:NOTES:END -->
