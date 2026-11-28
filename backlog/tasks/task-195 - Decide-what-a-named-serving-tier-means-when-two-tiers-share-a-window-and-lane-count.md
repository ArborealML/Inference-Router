---
id: TASK-195
title: "Bind exact recipe identity when context and lane counts coincide"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T07:34:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels:
  - config
  - scheduling
milestone: m-18
dependencies:
  - TASK-189
priority: medium
ordinal: 187000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Equal window/lane recipes may differ in weights, quantization, KV or resource settings. Own stable exact recipe identity and ambiguity refusal, not a second implementation of serving-instance verification. Numeric suitability may use a qualified resident independently of its display label.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Distinct effective settings have distinct stable identities; equivalent resolved aliases/defaults share identity.
- [ ] #2 Ambiguous exact launch selection refuses before mutation; selected recipe identity is frozen through the operation.
- [ ] #3 Numeric context-only selection can reuse a verified fitting resident without interpreting ambiguous display labels as proof.
- [ ] #4 SPEC §§5 and 7.6 distinguish suitability, display inference and exact identity; fixture tests cover equal numeric pairs.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TASK-118's completed exact-target identity evidence. Bind immutable effective recipe descriptors without duplicating live issuer/conformance work; TASK-118/197 own actual launch and first-write verification.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Former owner question addressed by the revision 5 reconciliation, subject to decision-83 ratification; not marked Done.
<!-- SECTION:NOTES:END -->
