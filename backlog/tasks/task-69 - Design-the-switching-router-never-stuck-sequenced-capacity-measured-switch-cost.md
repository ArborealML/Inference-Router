---
id: TASK-69
title: >-
  Design the switching router: never stuck, sequenced capacity, measured switch
  cost
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T01:24:00Z'
labels: []
dependencies: []
priority: high
ordinal: 69000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner's operating goal is recorded in decision-25: switching across the board, a router that is never stuck after a failed swap, capacity from sequencing models rather than running them together, and switch cost measured rather than assumed. This row is the design, and it comes before code: no scheduling, lifecycle or queue change lands from this goal until the plan is here and a design review at the tier this contract requires has returned. The design has to take a hard constraint seriously — four concurrent request lanes for the whole box, one engine loaded at a time — and it has to answer the posture rows this goal un-parks rather than route around them. Wake condition: the top-tier design pass's verdict, recorded in this row; the plan is drafted before the verdict and revised after it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the plan sits in this row before any code, and the design review at the required tier has returned and been dispositioned
- [ ] #2 the plan names what a failed swap does to in-flight and to waiting requests, in wire and feed terms
- [ ] #3 the plan honours four lanes for the whole box and one loaded model at a time, and says how capacity is sequenced
- [ ] #4 the posture rows this goal un-parks are answered or explicitly kept open by the plan, not worked around
<!-- AC:END -->
