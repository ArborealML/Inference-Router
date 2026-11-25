---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@chatswarm'
created_date: '2026-11-25T07:49:00Z'
labels: []
dependencies: []
priority: high
ordinal: 175000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A request that needs a 262144-token window must be served by an engine already resident at the wider-slot tier of the same window: a four-lane demand is satisfied by an eight-lane configuration, exactly as a 256k session is satisfied by a 1M-window engine. Today a named tier is matched by name, so a request that asks for the narrower-slot tier of the resident window forces a container relaunch. The owner's ruling: the client names model, required context and priority, and never concurrency; AIR picks the tier and resizes it when sustained demand exceeds concurrency. Design pass is dispatched to the external reasoning service; this task is Waiting on that result.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A request naming no tier is served on a resident tier whose measured window fits, whatever its slot count, with no switch.
- [ ] #2 Tier tie-breaks between equal windows prefer the greater reported slot count.
- [ ] #3 A client cannot name a serving tier on the inference path; the wire says so in SPEC §6.
- [ ] #4 AIR raises the slot tier when queued demand exceeds concurrency for a stated sustained period, and the rule is measured, not assumed.
- [ ] #5 Resize keeps in-flight work and queued work correct, and does not ping-pong between tiers.
- [ ] #6 The de-escalation non-goal in SPEC §7.6 is either replaced by a policy or restated with its scope.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Wake condition: the external design result lands on this task. Verified today: the scheduler compares a named tier by name equality against the resident serving snapshot, and the managed readiness check requires observed window and slots to equal the declared tier.
<!-- SECTION:NOTES:END -->
