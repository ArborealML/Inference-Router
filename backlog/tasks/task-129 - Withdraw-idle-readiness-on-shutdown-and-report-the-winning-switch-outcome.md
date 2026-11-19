---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T21:09:00Z'
updated_date: '2026-11-19T11:54:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent idle-withdrawal commit review dispatched November 19 wakes the coordinator. The focused Release test passed 1/1 at bed5864 with zero skips: the stopped engine retained its roster and withdrew readiness and first-write eligibility. Shutdown is preparing the separate injected completion arbitration plan.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Dispose a healthy idle runtime. Its previous model can remain listed, but IsReadyFor returns false and the runtime cannot issue a serving grant.
- [ ] #2 Pause after proof publication, let shutdown win settlement and resume. Observe one stopped outcome, zero switch-completed events and no admitted request under the retired proof.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse the existing capability withdrawal field set in the retrying versioned stop publication. Clear health and claim provenance while retaining roster and marker. One offline test starts with a successful idle probe, disposes the runtime, and checks the version, roster, withdrawn capability, model-only readiness and first-write refusal using a live token.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The independent plan review on dd44ca5 passed the idle fix. Historical waiter results use the committed window and delivery barrier independently of current health. Existing PRs #4, #8, #23 and #25 cover the legacy and staged winner orderings. Criterion 2 remains open for the older injected host path: it publishes proof while pending, then can emit completion after stop seals cancellation. Shutdown owns the separate follow-up plan for arbitration and a deterministic held-proof test. Ordinary RouterCore supplies no enrollment.
<!-- SECTION:NOTES:END -->
