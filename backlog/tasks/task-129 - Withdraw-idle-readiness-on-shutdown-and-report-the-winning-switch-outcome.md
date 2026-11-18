---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T21:09:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Idle disposal can preserve a healthy roster after withdrawing windows. Shutdown can also win settlement after readiness proof and before the completion event. Keep historical model listings independent from serving authority.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Dispose a healthy idle runtime. Its previous model can remain listed, but IsReadyFor returns false and the runtime cannot issue a serving grant.
- [ ] #2 Pause after proof publication, let shutdown win settlement and resume. Observe one stopped outcome, zero switch-completed events and no admitted request under the retired proof.
<!-- AC:END -->
