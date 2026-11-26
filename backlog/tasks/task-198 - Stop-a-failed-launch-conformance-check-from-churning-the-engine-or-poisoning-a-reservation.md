---
id: TASK-198
title: >-
  Stop a failed launch conformance check from churning the engine or poisoning a
  reservation
status: To Do
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-26T09:21:00Z'
labels:
  - lifecycle
  - scheduling
dependencies:
  - TASK-189
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
<!-- AC:END -->
