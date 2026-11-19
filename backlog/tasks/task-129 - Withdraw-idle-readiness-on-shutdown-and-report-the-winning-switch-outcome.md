---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T21:09:00Z'
updated_date: '2026-11-19T11:58:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted checks on [PR #26](https://github.com/ArborealML/Inference-Router/pull/26) and the verdict on the immediate host arbitration plan dispatched November 19 wake the coordinator. Idle withdrawal passed its bounded review and the named Release control passed 1/1 at bed5864. The reviewed diff matches the published commit.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Dispose a healthy idle runtime. Its previous model can remain listed, but IsReadyFor returns false and the runtime cannot issue a serving grant.
- [ ] #2 Pause after proof publication, let shutdown win settlement and resume. Observe one stopped outcome, zero switch-completed events and no admitted request under the retired proof.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse the existing capability withdrawal field set in the retrying versioned stop publication. Clear health and claim provenance while retaining roster and marker. One offline test starts with a successful idle probe, disposes the runtime, and checks the version, roster, withdrawn capability, model-only readiness and first-write refusal using a live token.

For the older injected host path, review exact claim and proof revalidation after the provisional publication hook, followed by terminal arbitration under the transition lock after host acceptance. Deliver the winning event outside the lock. Check whether the proposed single held-hook case distinguishes both stop intervals and observes full unwind.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The independent plan review on dd44ca5 passed the idle fix. Historical waiter results use the committed window and delivery barrier independently of current health. Existing PRs #4, #8, #23 and #25 cover the legacy and staged winner orderings. Criterion 2 remains open for the older injected host path: it publishes proof while pending, then can emit completion after stop seals cancellation. Shutdown owns the separate follow-up plan for arbitration and a deterministic held-proof test. Ordinary RouterCore supplies no enrollment.

Idle review confirmed the unconditional stop publication fences older snapshots and leaves claim-owned terminal results intact. The single idle case discriminates the stale model-only answer. Broader disposal race coverage was outside this change. The immediate host plan also accounts for cleanup exceptions converted to ordinary failed results after terminal selection.
<!-- SECTION:NOTES:END -->
