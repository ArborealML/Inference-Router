---
id: TASK-72
title: >-
  Drain the waiting queue by age-weighted value before switching, without
  ping-pong
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T19:01:00Z'
labels: []
dependencies:
  - TASK-69
  - TASK-25
  - TASK-51
  - TASK-71
priority: high
ordinal: 72000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
For a cohort of eight comparable-age requests for model B and one for A, serve all eight B requests, then A. The owner specified the service order, leaving initial residence unstated. Test the switch count with an explicit starting residence. Age must eventually protect a waiting A under continuing B arrivals.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Under steady competing load the router does not alternate switches between two models, pinned by a test that counts switches
- [ ] #2 Under arrivals that never stop, every waiting request keeps an age under a bound named in SPEC and switches per unit time stay bounded, with the refusal that enforces the bound on the feed
- [ ] #3 For eligible hosts, batching uses measured transition cost and hysteresis while the maximum-wait bound still protects aged requests. Demand and age cannot override a host refusal.
- [ ] #4 With B resident and eight B tickets plus one equally aged A ticket queued before dispatch, all eight B tickets finish before the switch to A. Record one B-to-A transition and successful A completion.
- [ ] #5 Repeat the fixed cohort with A initially resident. With the batch selected before dispatch, verify A-to-B then B-to-A, eight successful B completions before A, and no intervening switch.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A goal-level audit asked for the drain and admission rows (TASK-25, TASK-64), the queue ceiling (TASK-51) and the claim-correctness rows (TASK-67, TASK-68) to settle before this one; the two taken as dependencies are the ones this row consumes numbers from. Its warning that counting a switching ticket as inflight deadlocks the drain is already TASK-25's premise, not a new finding. The old #3 said only that an old wait is bounded; bounded now means an age bound and a switch-rate bound under arrivals that never stop.
<!-- SECTION:NOTES:END -->
