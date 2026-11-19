---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T21:09:00Z'
updated_date: '2026-11-19T12:32:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Shutdown is correcting the handoff between provisional runtime publication and actual immediate-host acceptance. Review of 094b8c0 confirmed stop can win after the callback releases the transition lock but before the host records acceptance. First-send admission still refuses. The three focused Release cases passed on that candidate; promotion awaits correction. Idle withdrawal landed in [PR #26](https://github.com/ArborealML/Inference-Router/pull/26).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Dispose a healthy idle runtime. Its previous model can remain listed, but IsReadyFor returns false and the runtime cannot issue a serving grant.
- [ ] #2 Pause after proof publication, let shutdown win settlement and resume. Observe one stopped outcome, zero switch-completed events and no admitted request under the retired proof.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
After the provisional proof hook, revalidate the exact pending claim and proof under transition. After host acceptance, select Ready and install its delivery signal together under transition. Deliver outside the lock; release waiters through cleanup even on exceptions. Serialize selection of Failed on a still-pending immediate-host refusal with stop. Preserve any selected stop or Ready when host I/O errors become failed return values. Exercise stop before acceptance, stop after acceptance before Complete, and converted cleanup failure after Ready. Await full unwind before final assertions.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Use one synchronous acceptance decision ordered with stop, keeping host I/O and notifications outside the transition lock. Stop after acceptance can still win before Complete. Add a deterministic control at the last handoff before the acceptance store; check that stop wins, no host commit or send occurs, guard and reservation are released, and one Cancelled attempt survives full unwind. Retain the existing postaccept and converted cleanup-refusal evidence. This finding was verified by source inspection on the injected path.
<!-- SECTION:NOTES:END -->
