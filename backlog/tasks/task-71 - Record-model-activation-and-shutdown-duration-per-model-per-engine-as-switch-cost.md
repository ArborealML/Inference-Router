---
id: TASK-71
title: >-
  Record model activation and shutdown duration per model per engine as switch
  cost
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-19T06:09:00Z'
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
- [x] #1 Activation and shutdown durations are measured per model per engine from a switch's own timings, not read from config
- [x] #2 The same arrivals pick a different model once the measured cost changes, pinned, so the number is an input to the choice and not a report
- [x] #3 Under bounded offered load, stale estimates or a single sample cannot postpone an eligible aged model past the documented scheduling bound. A deterministic test observes its successful dispatch.
- [x] #4 SPEC 5.5 or 7 says what the numbers mean and what they never decide alone
- [x] #5 5 Each stage is recorded as its own value, an unobserved stage is distinguishable from a zero, and a failed or cancelled attempt is still recorded with its outcome
- [x] #6 After a known eviction, discard the warm estimate. Report measured reload time for a matching cold profile, or report unknown when no matching observation exists. A fixture covers both outcomes.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace timing evidence from incumbent shutdown to verified target readiness. Identify where the scheduler compares eligible work and propose a deterministic cost-sensitive selection fixture with a starvation bound. Agree interfaces with host lifecycle and capability publication before coding. The coordinator runs the qualification tests.

Implement attempt-scoped monotonic stage observations with missing and failed outcomes distinct from zero. Commit the exact queue candidate selected or repeat selection. Built-in scheduling may compare complete eligible cost estimates. Custom scheduler calls retain their contract. A deterministic end-to-end test must change the chosen ticket and ensuing launch by changing only measured durations.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Landed in [PR #3](https://github.com/ArborealML/Inference-Router/pull/3) after independent review and green hosted CI. Single-engine cost dispatch changes the winning ticket when only measured activation changes. The inclusive thirty-second age boundary protects eligible work. Stage records distinguish absent, zero, incomplete and failed measurements; reload after eviction uses matching fresh samples or reports unknown. Multi-engine placement and production host authority retain their separate tasks.
<!-- SECTION:NOTES:END -->
