---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T21:09:00Z'
updated_date: '2026-11-19T11:42:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The verdict from the independent idle-shutdown plan review dispatched November 19 wakes the coordinator. Idle stop clears measured capacity but preserves health and the roster, so a model-only readiness query can still succeed. First-write admission already refuses stop.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Dispose a healthy idle runtime. Its previous model can remain listed, but IsReadyFor returns false and the runtime cannot issue a serving grant.
- [ ] #2 Pause after proof publication, let shutdown win settlement and resume. Observe one stopped outcome, zero switch-completed events and no admitted request under the retired proof.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Withdraw current health and its claim provenance in the versioned stop snapshot. Preserve the roster, switch marker and historical waiter result. One offline control observes eligibility before disposal and retained roster, ineligibility and refused first-write admission afterward.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Legacy and staged paths settle proof and terminal under the transition lock. Reuse the stop-first and commit-first evidence from PRs #4, #8 and #25. The older injected immediate publication releases the lock while pending and emits completion separately. Review this distinct ordering before closing criterion 2. Production RouterCore supplies no enrollment.
<!-- SECTION:NOTES:END -->
