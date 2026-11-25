---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@chatswarm'
created_date: '2026-11-25T07:49:00Z'
updated_date: '2026-11-25T08:51:00Z'
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
Wake: design pass landed 2026-11-25 (external pro tier); full document held outside the repository, summary ingested. Verified against this code before accepting: the named-tier path compares tier names, and the equal-window tie-break keeps declaration order — both confirmed here, both confirmed by the review. Accepted direction: check resident engines before ladder fit so an eight-slot resident answers a four-slot demand with no switch; make launch-tier choice slot-aware (never declaration order or label text); keep the launch proof exact (declared 4 / observed 8 stays a mismatch); let the caller supply only model, required context and optional priority. Accepted with limits: a resize controller that fences admission, drains, requires sustained over-capacity pressure and a dwell time tied to measured relaunch cost, caps elective resizes per model and per host, and aborts before any effect without killing inference. Deferred, named as separate designs: automatic shrinking, predicting performance before a launch, and fleet-wide claims (this task depends on the one-queue and host-placement work). Next action: write the implementation plan in six slices, ingress refusal first, then resident-dominant scheduling, then the resize controller.
<!-- SECTION:NOTES:END -->
