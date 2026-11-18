---
id: TASK-33
title: >-
  Document that the native Docker lifecycle provider is verified only against
  fakes
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:53:00Z'
updated_date: '2026-11-18T18:15:00Z'
labels:
  - engine
  - lifecycle
  - security
dependencies: []
priority: medium
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Parked by the owner at 17:55Z, superseding the 07:06Z provisional grant:** no lifecycle transaction on this host, no window, and the engine carrying these pi lanes is untouched. So the proof this row existed to run is deferred, and the answer that has to be written is the negative posture: the native provider is verified against fakes only. That is the remaining act — one SPEC 5.3.1 sentence naming the fakes-only posture and the refuse-don't-escalate rule (decision-1) — and it is the implementer's, not anyone's docker command.

**If a window is ever granted:** the bound stands unchanged — one named engine no other session uses, one model switch that actually drains it and starts another, nothing else touched, and a report naming what was started, stopped and removed with each phase's duration. If no engine can be shown idle, re-block rather than choosing which engine to disturb. `air config` prints endpoints, key-file locations and lifecycle argv, so the record is prose in a task, never a committed sample.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the router does not drive the live Docker daemon on this host: the answer is written into SPEC 5.3.1 as a posture, with the refuse-don't-escalate rule (decision-1) named
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The owner parked live engine mutation at 17:55Z. Document fake-provider verification in SPEC §5.3.1. The earlier provisional grant and report instructions are historical. Any future live proof requires separate authorization.
<!-- SECTION:NOTES:END -->
