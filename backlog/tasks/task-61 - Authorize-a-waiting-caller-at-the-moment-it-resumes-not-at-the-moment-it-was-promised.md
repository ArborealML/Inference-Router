---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T00:45:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The commit review of fd5e0cd returned REVISE; its findings were verified here before acceptance, and this row carries the three sharing one invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** In `Engines/EngineRuntime.cs` at a2cd859: (1) :647 re-checks `IsReadyFor`, then :650-652 returns Ready on the switch's own boolean when the target matched — and `EndSwitch` (:686-707) clears the marker and runs the dispatch hint before settling the readiness task, so a successor can claim the engine inside that window. (2) The fast path (:550) and the claim branch (:575) never re-check readiness in the gate, so two callers of one proved model launch it twice. (3) The hint at :705 runs before the settle at :706, so a throwing callback escapes into the catch at :825-833: a proven switch reads Failed, the engine unknown.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a waiter is told Ready only when the snapshot still authorises it at resume, pinned by a test where a second claim lands between the end commit and the resume
- [ ] #2 a failed own-target switch still ends Failed, not another turn of the loop, so a caller with no ceiling cannot relaunch forever
- [ ] #3 two callers of one already-proved model cause exactly one launch, with the claim decision re-checked inside the transition gate
- [ ] #4 the readiness task is settled before any injected callback runs, and a failing hint cannot rewrite the outcome of a completed switch
- [ ] #5 a forced CAS retry, a duplicate own-target claim, and an end-to-end dispatch through RouterCore are each pinned by a test
- [ ] #6 a thread-pool rejection at the claim (:602-610) and a shutdown during the claim both settle the readiness task and clear the marker, so no waiter waits on a switch that never ran
- [ ] #7 a claim hook that throws rolls the claim back instead of latching the engine as switching (:591-593 runs it inside the gate, after the state write, with no finally)
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Lands **last** in a three-commit set: TASK-63 AC #1 first, because :332's unfenced `PublishUnknown` can stamp `Healthy=false` from a poll that began before a switch proved the engine, and making Ready depend on the snapshot at resume would then fail requests served today. (a) `EndSwitch`: commit in the gate → settle `state.Ready` → the hint in its own `try`, its throw swallowed with a comment (latency only); every `Emit` already precedes `EndSwitch`, so decision-7's rule holds. (b) Claim branch: re-check `IsReadyFor` inside the gate immediately before `PublishSwitchStarted` (:591) and return Ready without claiming (AC #3). (c) At resume `ready` never yields Ready by itself: snapshot authorises → Ready; otherwise, if the target was ours, `SwitchingToward` set → TimedOut (RouterCore's existing wording), else Failed — the branch **ends** rather than looping, since the loop is bounded only by the caller's ceiling and an unbounded caller under alternating contention would relaunch forever (AC #1, #2). (d) `DisposeAsync` stops disposing `_switchLock` (:899; same reasoning as the undisposed `_switchCts` at :894-896, where an undisposed field already builds clean), and the waiter's two waits link `_switchCts.Token` so shutdown ends `cancelled`, not `ObjectDisposedException` → `engine_unreachable`; the transaction still runs on the token read at :582. Seams: wrap `AfterClaimForTests` (:32) so a throw rolls the claim back; add `AfterEndCommit` (post-commit, pre-settle, lock-free) and `BeforeLaunch`; AC #5's CAS retry is forced from inside `AfterClaim`. Wake condition: the sol-pro plan review's verdict, recorded in this row; code starts only after it.
<!-- SECTION:PLAN:END -->
