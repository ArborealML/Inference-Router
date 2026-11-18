---
id: TASK-66
title: 'Decision: should a failed health proof stop serving a wait_health=false model?'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-17T23:26:00Z'
updated_date: '2026-11-18T01:52:00Z'
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
- [x] #1 one line: keep the operator promise, or make a failed proof refuse the request
- [x] #2 the choice is recorded as a decision, naming the wire wording that moves if the posture changes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The owner answered with one word, verbatim: **"refuse"**, recorded as decision-27. A switch whose confirming observation cannot be made is not a proof: it emits the existing refusal kind with a reason naming what could not be confirmed, publishes the unknown capability, and ends the readiness task false. Serving continues for what the engine can still show. The pinned test named in this row moves with the code and the spec wording, in the same commit.
<!-- SECTION:NOTES:END -->
