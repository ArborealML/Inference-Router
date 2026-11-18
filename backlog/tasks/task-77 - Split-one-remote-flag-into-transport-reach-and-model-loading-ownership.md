---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T02:37:00Z'
labels:
  - lifecycle
  - scheduling
dependencies:
  - TASK-69
priority: high
ordinal: 77000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One flag answers two questions. remote says both that the router can reach the engine's HTTP API and that the router does not load models into it, which is why a host the router can talk to but cannot start is excluded from switching outright — a cluster host has no way to be switched today. The owner asked for this as a milestone row after the current safety-gate work: decision-25 makes sequenced capacity across hosts the router's goal, and this split is the precondition for any of it. TASK-69 settles the design; this row carries the change that follows from it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Config states transport reach and lifecycle ownership separately, and remote maps to them with a stated migration
- [ ] #2 An engine the router can reach but cannot start is switchable by a lifecycle provider that speaks to that host
- [ ] #3 The router never stops or launches an engine whose lifecycle it does not own, pinned
- [ ] #4 SPEC documents both flags and says what each one licenses
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Design direction to test in review, not to assume: two config keys, reach (can the router probe this engine) and lifecycle ownership (may the router start or stop it), with remote mapping to reach-without-ownership for existing configs and a stated migration. A switch would then be licensed by the lifecycle provider rather than by the absence of remoteness, so a provider that talks to a remote host over a transport can drive a switch there, while an engine nobody owns is never touched. Probing, admission and the wait_health posture all read reach, not ownership.
<!-- SECTION:NOTES:END -->
