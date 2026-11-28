---
id: TASK-214
title: "Qualify bounded metered commercial routing"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-203
  - TASK-207
  - TASK-208
  - TASK-209
  - TASK-210
  - TASK-211
  - TASK-212
  - TASK-200
  - TASK-217
  - TASK-218
priority: high
ordinal: 214000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qualify direct metered inference only after policy, stream, budget and account controls are ready and an explicit live-test authorization is recorded. Isoquant is the first target; public schema and fixtures are not account qualification.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record reviewed binary/config and actual account entitlement, model/capability and price bounds without publishing credentials or private configuration.
- [ ] #2 Controlled authorized stream and non-stream runs reconcile usage/charges, cancellation, refusal and unresolved exposure; no unauthorized replay or spend.
- [ ] #3 Private-sensitive and excluded requests never reach commercial capture endpoints; separately verify eligible ready private/external progress while another provider is held.
- [ ] #4 Same AOT release supports ordinary local forwarding and direct configured provider. Operator rollback and account disable are tested.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Fixture qualification first. Only perform live calls within the owner-approved limits in TASK-218 and with the privilege boundary in TASK-217. If no live spend is authorized, retain fixture-only status and park the live acceptance.
<!-- SECTION:PLAN:END -->
