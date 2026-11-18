---
id: TASK-69
title: >-
  Design the switching router: never stuck, sequenced capacity, measured switch
  cost
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T02:11:00Z'
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Drafting in progress. The plan has to settle these, in order. (1) Cost as data: a per-model, per-engine record of activation and shutdown duration, written from a switch's own timings, with a validity rule for a cold or single sample (TASK-71). (2) Cost as policy: the next model is chosen by the queue value it unlocks per second of switch time, weighted by request age; the drain rule is the owner's example — eight waiting on another model are served before one waiting on the loaded model (TASK-72). (3) Never stuck: a failed swap leaves the engine serving what it can show, the queue keeps its ceiling, and no switch holds the claim while it retries (TASK-64, TASK-25, TASK-51 are the parked corners). (4) Fairness floor: a cost-minimising scheduler still cannot starve a model, so a named maximum wait exists and the refusal enforcing it is observable. (5) Split the reach question: where an engine is reachable from (this box or another host in a cluster) is transport, and who may load a model into it is lifecycle ownership. Today one flag means both, and a remote engine simply refuses to switch. (6) Out of scope but constraining: one local forwarder, four lanes box-wide, one loaded model; decisions are taken from capability snapshots only, never from three separate readings.
<!-- SECTION:PLAN:END -->
