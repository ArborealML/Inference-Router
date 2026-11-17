---
id: TASK-55
title: Close the probe and switch publication races the 20a06fa review found
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 55000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The deep review of 20a06fa returned REVISE with six findings; the coding session verified all six against this code and its tests and none is accepted as a gap. Four are publication honesty in Engines/EngineRuntime.cs, and they share one failure shape: a write that publishes state a reader was not entitled to see. (1) HealthLoop awaits the model fetch and then publishes (:213-214); the compare-and-swap preserves the switch marker but not the list, so a fresher list published by a switch mid-await is replaced by the older one (:144-151) — bounded by one health interval, and it takes a model visible only in a switch-published list to turn that into a wrong 503. (2) EndSwitch writes idle switch state before clearing the switch marker and without holding _switchLock (:551-558), so a new arrival can install its own switch and have its marker nulled by the switch that just finished; blast radius is bounded because switch targets are always catalog-declared and the scheduler reads _switchState. (3) A wait_health=false switch whose model refresh fails swallows the error, emits engine.switch_completed and clears the marker on a stale list (:629-644). (4) Every ordinary poll bumps the capability version because the fetched array is always a new instance, so the scan's stability check never settles and the refusal 20a06fa shipped rarely fires — the docstring says the opposite. Plan review is in flight; the code change follows it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a probe publication landing during a switch is gated: the switch's list is never replaced by an older fetch
- [ ] #2 EndSwitch clears the marker before writing idle state, pinned by a barrier test on a second arrival's marker
- [ ] #3 two identical successive probes leave the capability version unchanged, so the scan's stability check settles
- [ ] #4 a switch whose model refresh fails publishes Healthy=false (unknown) rather than a completion on a stale list
- [ ] #5 SPEC 5.5 and a revision-log line state the publication rule and the unknown-on-failure posture
<!-- AC:END -->
