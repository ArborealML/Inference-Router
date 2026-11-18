---
id: TASK-33
title: >-
  Document that the native Docker lifecycle provider is verified only against
  fakes
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:53:00Z'
updated_date: '2026-11-18T18:07:00Z'
labels:
  - engine
  - lifecycle
  - security
dependencies: []
priority: medium
ordinal: 32000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Parked by the owner at 17:55Z, superseding the 07:06Z provisional grant:** no lifecycle transaction on this host, no window, and the engine carrying these pi lanes is untouched. So the proof this row existed to run is deferred, and the answer that has to be written is the negative posture: the native provider is verified against fakes only. That is the remaining act — one SPEC 5.3.1 sentence naming the fakes-only posture and the refuse-don't-escalate rule (decision-1) — and it is the implementer's, not anyone's docker command.

**If a window is ever granted:** the bound stands unchanged — one named engine no other session uses, one model switch that actually drains it and starts another, nothing else touched, and a report naming what was started, stopped and removed with each phase's duration. If no engine can be shown idle, re-block rather than choosing which engine to disturb. `air config` prints endpoints, key-file locations and lifecycle argv, so the record is prose in a task, never a committed sample.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the router does not drive the live Docker daemon on this host: the answer is written into SPEC 5.3.1 as a posture, with the refuse-don't-escalate rule (decision-1) named
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Adopted posture (decision-20 accepted, decision-1's refuse-don't-escalate rule applies to authorization):** the answer is no — the router does not drive the live daemon on this host; the native provider talks to a socket the operator provisions, and anything that would start or stop containers stays an explicit human act under decision-14 §3. Nothing was dispatched to the external service for this one; it was decided against this repository's code and SPEC §5.3.1.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T11:04:00Z

---

Wording fix on the previous comment: the config for that run is a working file kept **out of the repository** (a committed sample would print engine endpoints and lifecycle argv, which the hygiene rule in AGENTS.md forbids). The record is the report — what was started, stopped and removed, and how long each phase took — plus a SPEC sentence naming what is now verified against a live daemon rather than a fake.

---

<!-- COMMENTS:END -->
