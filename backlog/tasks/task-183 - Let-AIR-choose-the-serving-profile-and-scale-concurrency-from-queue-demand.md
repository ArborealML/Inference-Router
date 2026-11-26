---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:49:00Z'
updated_date: '2026-11-26T00:41:00Z'
labels: []
dependencies:
  - TASK-190
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

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Six slices on one branch, ordered by risk. (1) Ingress: a client-named serving tier on the inference path is refused with an additive named reason; SPEC 6 states the client names model, required context and optional priority only. Existing error byte shape unchanged. (2) Resident-dominant scheduling: measure resident engines first, admit a request on any resident whose measured window fits whatever its slot count, consult the ladder only when no resident fits. Pin with a fake reporting an eight-slot resident against a four-slot demand and asserting zero lifecycle calls. (3) Equal-window tie-break prefers the greater reported slot count in SmallestFittingProfile, and launch-tier choice becomes slot-aware rather than declaration order; shared with the startup-tier task. (4) Resize controller, needing drain-before-teardown: fence admission, require sustained over-capacity pressure over a stated window plus a dwell time tied to measured relaunch cost, cap elective resizes per model and per host, and abort before any effect rather than killing inference. Upward only; shrinking stays a named non-goal. (5) Telemetry: each elective resize records trigger, measured queue depth and dwell; both gauge directions pinned. (6) SPEC 7.6 de-escalation restated with its scope, plus the revision log. Gates: build with warnings as errors, focused scheduler, lifecycle and wire tests, and the shipped-sample config test.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design pass landed 2026-11-25 (external pro tier); document held outside the repository. Both defects it cited are confirmed in this code: the named-tier path compares tier names, and the equal-window tie-break keeps declaration order. Resize needs the drain machinery, so this task now depends on it. Next action: plan review of the six-slice plan, dispatched 2026-11-26; implementation starts after the containment and stream-cause slices land.
<!-- SECTION:NOTES:END -->
