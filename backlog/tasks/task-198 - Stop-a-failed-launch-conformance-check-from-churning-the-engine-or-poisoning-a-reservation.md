---
id: TASK-198
title: >-
  Stop a failed launch conformance check from churning the engine or poisoning a
  reservation
status: To Do
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-26T09:21:00Z'
updated_date: '2026-11-26T12:15:00Z'
labels:
  - lifecycle
  - scheduling
dependencies:
  - TASK-189
  - TASK-190
  - TASK-197
priority: high
ordinal: 190000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
When an exact conformance check now fails, the container the launch started keeps running, the group records no resident and no quarantine, and the next ticket for the same engine repeats the whole launch-verify-fail cycle — an unbounded series of roughly two-minute relaunches on the host's only healthy engine. On the staged-host path the same failure leaves the verified route closed with a durable pending operation, so retries re-verify the same bad instance and a different target meets a pending-operation conflict. Decide and implement the cleanup after a refused publication: retire or quarantine the offending instance, clear or reconcile the pending state, and record a reason an operator can act on. Separately, state in SPEC whether a stored readiness proof stays authoritative when the observed lane count changes underneath it, since today it is retained when the model, window and label still match.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A refused launch publication leaves no running instance that the next ticket will relaunch against: the instance is retired or quarantined and a later ticket does not repeat the launch-verify-fail cycle.
- [ ] #2 Two tickets in sequence against the same mismatching instance produce a bounded number of provider launches, pinned by a counted-call test.
- [ ] #3 After a staged-host refusal, the reservation is reconciled so a different target can proceed and the same target does not re-verify the same bad instance forever.
- [ ] #4 SPEC states whether a stored readiness proof survives a change in observed lane count, and one test pins the chosen behaviour.
- [ ] #5 A refused launch persists a disposition tied to its operation and instance that adoption, recovery and inventory cannot clear, adopt, or read as vacant.
- [ ] #6 Any automatic disposal persists its intent before I/O, stops only that instance id, proves it absent, and replays idempotently after a crash or takeover.
- [ ] #7 When the container runtime and the probe disagree about what an engine is, the disagreement is reported on the feed without restarting anything.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The lane-drift site to pin for criterion 4 is WithModelsObservation, EngineRuntime.cs:474-492, which keeps a stored readiness proof across a lane change when model, window and profile match. The design pass on the launch-conformance work counted the retry as unbounded today: the two adoption-loop inspections and the local owner timeout are per-ticket bounds, not physical launch bounds, so criterion 2 is pinned by counted provider calls and live inventory rather than by 503 counts or switch events.
<!-- SECTION:NOTES:END -->
