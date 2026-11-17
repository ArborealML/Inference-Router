---
id: TASK-63
title: >-
  Fence the negative publish and the gauge updates, not only the successful
  probe
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
labels:
  - concurrency
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
- [ ] #1 a negative publish from a stale generation is dropped, pinned by a test
- [ ] #2 a window or slot update from a stale /props response is not applied, pinned by a test
- [ ] #3 SPEC 5.5's quiet-poll wording says what the comparison actually does rather than describing the published reference
<!-- AC:END -->
