---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:49:00Z'
updated_date: '2026-11-26T01:11:00Z'
labels: []
dependencies:
  - TASK-190
priority: high
ordinal: 175000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A request that needs a 262144-token window must be served by a resident engine at the wider-slot tier of that window: a four-lane demand is satisfied by an eight-lane configuration, as a 256k session is satisfied by a 1M-window engine. Today a named tier is matched by name, so a request for the narrower-slot tier of the resident window forces a relaunch of about 110 s. Owner ruling: the client names model, required context and priority and never concurrency; AIR picks the tier. The decision to raise a tier, and the lifecycle act of raising it, are carried by the two tasks below.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A request naming no tier is served on a resident tier whose measured window fits, whatever its slot count, with no switch.
- [ ] #2 Tie-breaks between equal windows prefer the greater reported slot count.
- [ ] #3 A client cannot choose a serving tier on the inference path; a legacy name is honoured only as a context minimum, per SPEC 6.
- [ ] #4 The context requirement is the greater of the client minimum and the measured prompt plus output reservation; an omitted header uses the computed value.
- [ ] #5 A fully occupied resident queues a compatible request instead of escalating the ladder.
- [ ] #6 Satisfaction is judged apart from launch conformance: declared-4 observed-8 stays a fault, and a verified 8-slot resident serves a 4-slot demand with zero lifecycle calls.
- [ ] #7 The status surface separates desired, observed and admissible capacity and names which one binds.
- [ ] #8 The de-escalation non-goal in SPEC 7.6 is replaced by a policy or restated with its scope.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reordered after the design review; one branch, and resizing stays disabled until the transition work lands. (1) Ingress compatibility first: while clients migrate, a recognised legacy tier name becomes a context minimum and is never carried into desired runtime identity; an unrecognised or undersized choice keeps today's refusal. (2) Normalise the requirement: the demand is the greater of a validated client minimum and the rendered prompt, template and tool tokens plus an enforced output reservation; an omitted minimum uses the computed value, unknown is not zero, and a cache hit does not shrink it. (3) Resident-dominant scheduling: check residents before ladder fit, admit on measured window fit whatever the slot count, and use one capability predicate through reservation and admission so no later check defeats it. (4) Busy is not incompatible: a full eight-slot resident queues a ninth compatible request instead of escalating the ladder, and incompatible queue heads are bypassed. (5) Satisfaction apart from launch conformance: the declared-versus-observed proof stays exact, an observed superset is accepted only with its guarantees qualified, and drift diagnostics remain.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design pass landed 2026-11-25 and its plan review 2026-11-26, both external pro tier; documents held outside the repository. Both code claims the review cited are confirmed here: named tiers match by name, and the equal-window tie-break keeps declaration order. Accepted: legacy names translate rather than refuse, because the deployed client always sends one; the capability predicate must reach through reservation and admission, since a later check can defeat resident selection; satisfaction is separated from launch conformance rather than loosening the declared-equality proof; a busy resident means queue, not escalation; and which capacity limit binds is a criterion, not an assumption. The review split the resize rule and the fencing into two other tasks. Deferred: automatic shrinking, pre-launch performance prediction, fleet-wide claims.
<!-- SECTION:NOTES:END -->
