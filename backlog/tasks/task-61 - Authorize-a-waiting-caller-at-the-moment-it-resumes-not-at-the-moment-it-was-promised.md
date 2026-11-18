---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T02:56:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner decided the posture this row was waiting on: a switch the engine cannot confirm is **refused** (decision-27), so the capability and the answer to the caller can no longer disagree. The invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** In Engines/EngineRuntime.cs the wait loop re-checks readiness and then returns Ready on the switch's own boolean anyway when the target matched, while EndSwitch clears the marker before settling the readiness task, so a successor can claim the engine inside that window and the first waiter is sent a model the successor is about to unload. A top-tier plan review of this row's plan changed its shape, and the code confirms why: a snapshot read at resume is a point check, so it narrows that window rather than closing it, and refusing a waiter whose own switch *succeeded* contradicts decision-9, which says a caller overtaken by another target takes its own turn.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The cannot-confirm refusal lands first in its own commit as TASK-62 criterion #2; this row changes only how a resuming waiter is classified
- [ ] #2 A switch whose own attempt ended false ends Failed once, so a caller with no ceiling cannot relaunch forever
- [ ] #3 A waiter whose own attempt succeeded but whose snapshot no longer authorises the target takes its own turn rather than a refusal, pinned where a second claim wins the engine
- [ ] #4 The resume check is pinned by a gate between the readiness await and the snapshot read, so the pin cannot pass on thread scheduling
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Corrected shape, from the top-tier plan review and confirmed against the code. The cannot-confirm refusal lands first, in TASK-62's commit. At resume there are three answers, in this order: the snapshot authorises the target, so Ready; the switch's own attempt ended false, so Failed once, because nothing retries internally and a caller with no ceiling would otherwise relaunch forever; the attempt succeeded but the snapshot no longer authorises the target, so the caller takes its own turn through the claim path exactly as decision-9 prescribes for a caller overtaken by another target, bounded by its own ceiling rather than by a refusal. The pin sits between the readiness await and the snapshot read: park waiter A there, let a second claim win the engine, release A, and assert A is not told Ready. Do not read the resume check as the entitlement — authority held across the handoff is the reservation row's criterion.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**What the entitlement really needs is not this row.** Verified against RouterCore: after the wait returns Ready the request is enqueued with no revalidation, so an authority re-read at resume can be stale before the engine ever receives the request. Holding it across the handoff belongs to the reservation row (TASK-25) and the admission/drain row (TASK-64); this row's check stays as defence in depth, and TASK-25 now carries the criterion. **Ordering:** the cannot-confirm refusal lands first, in TASK-62's commit. Landing the resume classification first would mislabel an unconfirmed attempt that still settles true as "superseded by a later switch", which is exactly the wire-versus-feed disagreement decision-27 exists to remove.
<!-- SECTION:NOTES:END -->
