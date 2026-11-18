---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T00:53:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Blocked on the owner's answer to TASK-66** (whether a failed health proof stops serving a `wait_health=false` model): the plan review of the set showed that resume-time authorisation and today's posture cannot both hold. A `wait_health=false` switch whose refresh failed publishes unknown, emits `engine.switch_completed` and ends ready (:794-812), and that outcome is pinned at CapabilityPublicationTests.cs:405-410 — so authorising at resume necessarily turns it into a refusal on the wire while the feed says completed. The rest of the set is unaffected and lives in TASK-67.

The invariant this row still carries: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** Today :647 re-checks `IsReadyFor` and :650-652 returns Ready on the switch's own boolean anyway when the target matched, while `EndSwitch` (:686-707) clears the marker before settling the readiness task, so a successor can claim the engine inside that window and the first waiter is sent a model the successor is about to unload.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the failed posture the owner chose is implemented in the same commit as the resume check, with its test and its wire wording moved together
- [ ] #2 a failed own-target switch still ends Failed, not another turn of the loop, so a caller with no ceiling cannot relaunch forever
- [ ] #3 a waiter is told Ready only when the snapshot still authorises it at resume, pinned where a second claim lands between the end commit and the resume; an unsuperseded switch still returns Ready and nudges dispatch once
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Blocked: the answer decides which of these shapes is legal, and both are coherent. (1) No refresh means no proof — `engine.switch_failed`, unknown, `EndSwitch(false)`; the pinned test and the wire wording move in that approved commit. (2) Keep the promise, and make the snapshot say what actually happened: the switch vouches for a target it launched but could not query, as a distinct state in the capability, and the resume check accepts a vouch only while the snapshot version it was recorded at is unchanged. `Ready` with `Healthy=false` is not a shape: it is the hole. Once answered, the resume branch is: snapshot authorises (or a live vouch covers it) → Ready; otherwise, if the target was ours, **Failed** with a "superseded by a later switch; retry" detail — not `TimedOut`, because nothing here retries automatically and a caller with no ceiling would otherwise wait forever on a turn it will never get (AC #2). Landing after TASK-63 AC #1 and TASK-67.
<!-- SECTION:PLAN:END -->
