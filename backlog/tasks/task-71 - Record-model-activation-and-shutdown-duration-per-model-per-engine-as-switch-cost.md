---
id: TASK-71
title: >-
  Record model activation and shutdown duration per model per engine as switch
  cost
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T22:58:00Z'
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
Record queue, shutdown, load, confirmed readiness, first forwarding and terminal outcome separately. Distinguish unobserved duration from zero and classify failed, cancelled or overtaken attempts. Estimates influence selection only among eligible targets.

Compare drain plus verified shutdown of A and activation of B against actual handoff time, keyed by host, engine version, model digest and effective config. Distinguish cold from warm samples. Track timeouts and failures separately from successful durations. Evaluate the proposed 64-success, 30-day recency-weighted percentile window against recorded workloads before fixing its parameters.

External plan review dispatched. The candidate measures monotonic stages under one claim identity, preserves missing and failed observations, and compares eligible switches using a bounded cost history. The acceptance fixture keeps arrivals fixed and changes only the measured cost.

The [integrated scheduler](https://github.com/ArborealML/Inference-Router/commit/a77d50a) now commits the exact thread-admissible ticket it evaluated. A blocked-head case and cancellation during selection both failed on the preceding dispatch implementation and passed after the change. A custom scheduler retained its call ordering. The combined filter passed 45 cases and the full Release suite passed 442. The cost history and ranking functions still require production stop/launch observations and a test changing the actual launch.

Combined scheduling and capacity candidate passed 237 of 238 focused Release cases. The eligible loaded-route test selected a different ticket. The scheduling implementer owns diagnosis and correction before integration.
<!-- SECTION:NOTES:END -->
