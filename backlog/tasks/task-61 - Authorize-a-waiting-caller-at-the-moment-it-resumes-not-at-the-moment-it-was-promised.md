---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The commit review of fd5e0cd returned REVISE with five blocking findings; all five were verified in the code before acceptance, and this row carries the three that share one invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** Three defects, each verified at the cited lines. (1) EngineRuntime.cs:647-659 returns Ready from the switch's own boolean without re-checking IsReadyFor; EndSwitch clears the marker and signals dispatch before settling the task (:686-706), so a second switch can claim the engine in that window and the first waiter is told Ready for a model a successor is about to unload, and RouterCore.cs:527-566 then sends. (2) The readiness fast path at :547-594 is evaluated before the switch lock and the idle branch claims without re-checking, so a second caller for an already-proved model, parked on the lock while the first succeeds, sees idle state and relaunches an engine that already serves it (pre-existing). (3) EngineRuntime.cs:705-706 runs the dispatch hint before settling the readiness task, so a throwing injected callback strands every waiter until its ceiling; SignalDispatch only releases a semaphore today, which is why CI cannot see it. A plan review of the fix set is dispatched before code.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a waiter is told Ready only when the snapshot still authorises it at resume, pinned by a test where a second claim lands between the end commit and the resume
- [ ] #2 two callers of one already-proved model cause exactly one launch, with the claim decision re-checked inside the transition gate
- [ ] #3 the readiness task is settled before any injected callback runs
- [ ] #4 a forced CAS retry, a duplicate own-target claim, and an end-to-end dispatch through RouterCore are each pinned by a test
<!-- AC:END -->
