---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T01:52:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner decided the posture this row was waiting on: a switch the engine cannot confirm is **refused** (decision-27), so the capability and the answer to the caller can no longer disagree. The invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** In Engines/EngineRuntime.cs: the wait loop re-checks readiness at :647 and then :650-652 returns Ready on the switch's own boolean anyway when the target matched, while EndSwitch (:686-707) clears the marker before settling the readiness task, so a successor can claim the engine inside that window and the first waiter is sent a model the successor is about to unload.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the failed posture the owner chose is implemented in the same commit as the resume check, with its test and its wire wording moved together
- [ ] #2 a failed own-target switch still ends Failed, not another turn of the loop, so a caller with no ceiling cannot relaunch forever
- [ ] #3 a waiter is told Ready only when the snapshot still authorises it at resume, pinned where a second claim lands between the end commit and the resume; an unsuperseded switch still returns Ready and nudges dispatch once
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Decided shape (decision-27): an unconfirmed switch is a failure, so the resume branch needs no special case for it. Resume: the snapshot authorises the target (or a live vouch covers it) → Ready; otherwise, if the target was ours, **Failed** with a "superseded by a later switch; retry" detail — not TimedOut, because nothing retries internally and a caller with no ceiling would wait forever on a turn it never gets (AC #2). Lands after TASK-63 AC #1 and TASK-67, and its commit also moves the pinned failed-confirmation test and the refusal wording together.
<!-- SECTION:PLAN:END -->
