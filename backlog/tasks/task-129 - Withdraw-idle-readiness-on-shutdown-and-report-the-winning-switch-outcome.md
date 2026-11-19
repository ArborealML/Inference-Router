---
id: TASK-129
title: Withdraw idle readiness on shutdown and report the winning switch outcome
status: In Progress
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-18T21:09:00Z'
updated_date: '2026-11-19T12:11:00Z'
labels: []
milestone: m-2
dependencies: []
priority: high
ordinal: 127000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Shutdown is implementing terminal arbitration for the older injected host callback. Idle shutdown landed in [PR #26](https://github.com/ArborealML/Inference-Router/pull/26); its focused test and hosted checks passed.
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
Independent plan review on 087d5b9 confirmed the provisional callback returns before host acceptance, and Complete is separate. The earlier single test could only witness the first interval. Use distinct host proof expectations for the two stop orders. A cleanup IOException can become a normal failed report after Ready; exception filters alone cannot preserve the winner. Reuse existing admitted/refused evidence. Ordinary RouterCore supplies no enrollment.
<!-- SECTION:NOTES:END -->
