---
id: TASK-197
title: >-
  Bind the launch target so a router-selected tier is verified as exactly as a
  named one
status: To Do
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-26T09:21:00Z'
labels:
  - scheduling
  - lifecycle
dependencies:
  - TASK-189
priority: high
ordinal: 189000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Conformance at launch is switched on only when the caller named a tier: both launch paths pass an expected profile solely when the request carries an explicit profile option, so a tier the router picked itself — the ordinary case for a context-only request — publishes Ready on a window-only proof. A launch instructed to start four lanes that comes up measuring eight is then certified, a completion event is emitted, and the instance is dispatched against a capacity nobody asked the provider to start. Bind the expected window and lane count to whatever tier the launch targeted, named or selected, and report a capacity mismatch as its own named reason rather than as a window shortage, which today tells the operator the context is too small when the lanes are what differ.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A launch targeting a concrete tier fails conformance when the observed lane count differs from that tier's declaration, whether the tier came from the request or from the router's own selection.
- [ ] #2 One test names a tier and one selects it, each observing extra lanes, and both assert no Ready publication, no completion event and no forward.
- [ ] #3 The refusal names the mismatch as expected and observed lane count, distinct from the existing window-too-small reason, and the reason is additive to the §6.1 list.
<!-- AC:END -->
