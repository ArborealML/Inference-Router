---
id: TASK-33
title: 'Decision: may the router drive the live Docker daemon on this host'
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T07:53:00Z'
updated_date: '2026-11-17T14:48:00Z'
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
**Answer recorded: allow ONE bounded lifecycle transaction on this host.** The remaining act is performing it and
reporting — the coding session, about 20 minutes — and it is a mutation of shared infrastructure, so the report
names what was started, stopped and removed and how long each phase took.

**The bound, or it does not run:** one named engine that no other session is using; one model switch that actually
drains it and starts another; nothing else touched. If no engine can be shown idle, stop and re-block this task
rather than choosing which engine to disturb.

**Config stays out of the repository:** `air config` prints engine endpoints, key-file locations and lifecycle
argv, so a committed sample would publish them. The record is the report, plus one SPEC sentence saying what is now
verified against a live daemon rather than a fake.

**Links:** the route already settled: [TASK-20](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-20%20-%20Decision-how-the-router-starts-and-stops-engines.md); provider behaviour and its fakes: [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §5.3.1 and [tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs](https://github.com/ArborealML/Inference-Router/blob/main/tests/Arboreal.InferenceRouter.Tests/DockerLifecycleProviderTests.cs); the criteria this verifies live: [TASK-26](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-26%20-%20Close-the-gaps-SPEC-5.3.1-still-describes-for-container-native-lifecycle.md).

**If it is declined instead:** say so in SPEC, because "the native provider is verified only against fakes" is a
posture the README should state rather than leave implied.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: allow one lifecycle transaction on this host, or no live daemon runs and verification stays on fakes
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Adopted posture (decision-20 accepted, decision-1's refuse-don't-escalate rule applies to authorization):** the answer is no — the router does not drive the live daemon on this host; the native provider talks to a socket the operator provisions, and anything that would start or stop containers stays an explicit human act under decision-14 §3. Nothing was dispatched to the external service for this one; it was decided against this repository's code and SPEC §5.3.1.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T11:04:00Z

---

Wording fix on the previous comment: the config for that run is a working file kept **out of the repository** (a committed sample would print engine endpoints and lifecycle argv, which the hygiene rule in AGENTS.md forbids). The record is the report — what was started, stopped and removed, and how long each phase took — plus a SPEC sentence naming what is now verified against a live daemon rather than a fake.

---

<!-- COMMENTS:END -->
