---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: Waiting
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T19:33:00Z'
labels:
  - lifecycle
  - scheduling
milestone: m-6
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
- [ ] #5 An omitted remote never grants load authority: ownership comes from a target-bound lifecycle provider, never from a hostname or from a missing key
- [ ] #6 A shipped sample engine with no lifecycle block still refuses to switch after the migration, pinned by the sample test
- [ ] #7 A legacy remote endpoint is routable without actuation or adoption. A foreign endpoint sharing a managed host blocks activation until the host authority proves vacancy.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The reviewed migration must preserve the shipped sample with no lifecycle block. The loader currently maps an omitted remote key to false, so load authority must come from an explicit target-bound provider. A routable unmanaged engine can serve a resident model. A request for an absent model currently waits for capacity (TASK-51). Decision-56 requires one serving instance per model. TASK-110 carries host-exclusive authority. Wake when the external review of the migration plan returns. It covers presence-aware legacy mappings, delegated provider binding and host admission.
<!-- SECTION:NOTES:END -->
