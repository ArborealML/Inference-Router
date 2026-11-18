---
id: TASK-71
title: >-
  Record model activation and shutdown duration per model per engine as switch
  cost
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T23:13:00Z'
labels: []
milestone: m-5
dependencies:
  - TASK-69
priority: high
ordinal: 71000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Switch cost stops being an assumption. Per model per engine, the router already knows when a switch started, when the target was first proved serving, and when the previous model stopped: those become a measured activation and shutdown duration rather than a log line. Where the numbers live, how long they stay valid, and exactly how the scheduler uses them are decided by the design row, not here; this row carries the deliverable and its tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Activation and shutdown durations are measured per model per engine from a switch's own timings, not read from config
- [ ] #2 The same arrivals pick a different model once the measured cost changes, pinned, so the number is an input to the choice and not a report
- [ ] #3 Under bounded offered load, stale estimates or a single sample cannot postpone an eligible aged model past the documented scheduling bound. A deterministic test observes its successful dispatch.
- [ ] #4 SPEC 5.5 or 7 says what the numbers mean and what they never decide alone
- [ ] #5 5 Each stage is recorded as its own value, an unobserved stage is distinguishable from a zero, and a failed or cancelled attempt is still recorded with its outcome
- [ ] #6 After a known eviction, discard the warm estimate. Report measured reload time for a matching cold profile, or report unknown when no matching observation exists. A fixture covers both outcomes.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace timing evidence from incumbent shutdown to verified target readiness. Identify where the scheduler compares eligible work and propose a deterministic cost-sensitive selection fixture with a starvation bound. Agree interfaces with host lifecycle and capability publication before coding. The coordinator runs the qualification tests.

Implement attempt-scoped monotonic stage observations with missing and failed outcomes distinct from zero. Commit the exact queue candidate selected or repeat selection. Built-in scheduling may compare complete eligible cost estimates. Custom scheduler calls retain their contract. A deterministic end-to-end test must change the chosen ticket and ensuing launch by changing only measured durations.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [integrated scheduler](https://github.com/ArborealML/Inference-Router/commit/a77d50a) commits the exact thread-admissible ticket selected. The measured-cost candidate records monotonic stages on the same claim, separates failed or missing observations, and compares only eligible switches. The combined Release filter passed 241 cases with zero skips. With the same arrivals and switch costs, a loaded model with a proven window ends comparison and A launches; with its window unknown, comparison reaches B and B launches. This corrects the earlier mixed-queue fixture. Independent composition and fixture reviews precede integration. Compare measured drain, shutdown and activation against real handoffs by host, engine version, digest and effective config. Evaluate sample retention against workloads. Physical admission, exact instance routing, deadline enforcement and live swaps retain separate gates.

Review found token pressure is captured for ranking but omitted from final revalidation. Test whether pressure changes at equal inflight count alter the winner. Verify shutdown sample identity and single attribution across provisional and terminal observations before integration.
<!-- SECTION:NOTES:END -->
