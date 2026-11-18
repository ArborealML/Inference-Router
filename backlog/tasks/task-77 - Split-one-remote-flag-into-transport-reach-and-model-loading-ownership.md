---
id: TASK-77
title: Split one remote flag into transport reach and model-loading ownership
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T02:37:00Z'
updated_date: '2026-11-18T20:50:00Z'
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
A router must be able to forward inference to a resident model and independently decide whether it can request a switch on that host. Split transport from lifecycle authority, preserve legacy configurations, and prove a managed peer can switch under its own host authority.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Config declares the inference endpoint and lifecycle owner independently. TOML and JSON tests pin legacy remote mapping for omitted, false and true.
- [ ] #2 An engine the router can reach but cannot start is switchable by a lifecycle provider that speaks to that host
- [ ] #3 The router never stops or launches an engine whose lifecycle it does not own, pinned
- [ ] #4 SPEC documents which requests the inference endpoint can serve and which lifecycle actions each owner can authorize.
- [ ] #5 An omitted remote never grants load authority: ownership comes from a target-bound lifecycle provider, never from a hostname or from a missing key
- [ ] #6 A shipped sample engine with no lifecycle block still refuses to switch after the migration, pinned by the sample test
- [ ] #7 A legacy remote endpoint is routable without actuation or adoption. A foreign endpoint sharing a managed host blocks activation until the host authority proves vacancy.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use the configured endpoint for inference transport. Normalize target-bound lifecycle ownership once, preserving public construction and legacy config semantics. Validate authority when claiming an idle engine, preserve joins, and verify external forwarding and exact no-owner refusal. Exercise TOML/JSON, samples, provider injection and Release AOT. Next distinguish pre-mutation refusal from uncertain mutation, preserve the incumbent when justified, and prefer eligible targets. Complete peer switching through the host arbiter after TASK-110 with positive and denied-operation tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan review against 49219b3 confirmed nullable raw Remote, public boolean Remote and the early runtime refusal. The first implementation stage keeps admission unchanged: a healthy unloaded candidate can return no_lifecycle_owner. Filtering it prematurely could leave the ticket queued. The DTO must detect conflicting keys even when remote is explicitly false. A provider instance alone grants no permission. The peer proof and decision-62 failure policy remain acceptance work.

Astra ran 112 focused checks on owner bindings and 61 on incumbent restoration; both groups passed. Separate commit reviews cover production, tests and pre-mutation refusal. Confirm the operator diagnosis for mismatched injected providers and document the selectorless legacy refusal before integration.

The combined candidate reached local Release qualification. CLI owner diagnostics failed nullable analysis before test execution; correction assigned. Integration reviews of the runtime and fixtures are in flight.
<!-- SECTION:NOTES:END -->
