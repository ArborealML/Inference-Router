---
id: TASK-62
title: Refuse a switch proof that never named the requested model
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
labels:
  - concurrency
  - wire
dependencies: []
priority: high
ordinal: 62000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router can vouch for a model nothing ever reported. EngineRuntime.cs:787-810: with wait_health=false a successful /models fetch is published as a proof and the switch ends as ready whether or not the reported list names the requested model, and even a failed fetch ends the switch as ready. SPEC 5.5 already says an engine the router cannot check ends unknown, so the code is what is wrong here, not the SPEC: wait_health=false skips the health.expect rule, never the check that the engine says it holds this model. The existing test pins the wrong behaviour — CapabilityPublicationTests.cs:387-413 asserts Ready together with unhealthy — and must move with the fix rather than stay as a record of the bug.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a refresh whose reported list omits the target ends switch_failed with an actionable reason, Healthy=false, and no Ready
- [ ] #2 a failed refresh ends the switch unverified rather than ready
- [ ] #3 the test at CapabilityPublicationTests.cs:387-413 is rewritten to the corrected posture, and the exec's own success still appears on the feed
<!-- AC:END -->
