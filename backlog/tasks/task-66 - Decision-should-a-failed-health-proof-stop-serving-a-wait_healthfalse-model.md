---
id: TASK-66
title: 'Decision: should a failed health proof stop serving a wait_health=false model?'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-17T23:26:00Z'
updated_date: '2026-11-18T00:54:00Z'
labels:
  - release
  - governance
dependencies: []
priority: medium
ordinal: 66000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
For an engine configured wait_health=false — one the operator has told the router not to health-check — a failed /models refresh today ends the switch **ready**: the caller gets its request, and the capability is marked unknown. The provider interface states this as an operator promise made in configuration, not a fallback the router may choose for itself, and a test pins it. The alternative is to end such a switch failed and refuse the request. That is operator-visible for every engine using the setting, so it is a decision and not a bug fix. What changes under each: **keep** — the router never overrides the config promise, but a switch can vouch for a model nothing reported, and the only protection is the unknown flag that stops the *next* dispatch; **change** — nothing is served on an unverified proof and the refusal wording, the feed event and the config's meaning all move, which is a wire and documentation change for a public surface. Recommendation from the coding session: keep the promise, and let TASK-62 close the part that is actually a bug — a refresh that names the model is a proof, one that omits it is not.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 one line: keep the operator promise, or make a failed proof refuse the request
- [ ] #2 the choice is recorded as a decision, naming the wire wording that moves if the posture changes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**This answer now gates a scheduled commit, as measured at d3258d0.** A `wait_health=false` switch whose refresh failed publishes unknown, emits `engine.switch_completed` and ends ready (:794-812). Authorising a waiting caller against the snapshot at resume (TASK-61) cannot coexist with that: the request would be refused on the wire while the feed says completed. Two coherent shapes: no-refresh means no proof (refusal, unknown, switch ends failed — the pinned test and the wire wording move together), or the switch vouches for a target it launched but could not query, as its own state in the capability, with the vouch valid only while that snapshot version is unchanged. `Ready` with health unknown is not a third shape; it is the hole TASK-61 exists to close.
<!-- SECTION:NOTES:END -->
