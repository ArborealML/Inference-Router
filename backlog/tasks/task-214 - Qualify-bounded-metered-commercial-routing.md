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
Qualify direct bounded metered inference for the explicitly authorized account; synthetic integrated tests land in TASK-212 first. No Pi UX or subscription prerequisite. Live calls need affirmative authorization, complete fail-closed egress controls and external privilege contract for the supported policy mode.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record reviewed binary/config and actual account entitlement, model/capability and price bounds without publishing credentials or private configuration.
- [ ] #2 Controlled authorized stream and non-stream runs reconcile usage/charges, cancellation, refusal and unresolved exposure; no unauthorized replay or spend.
- [ ] #3 Private-sensitive and excluded requests never reach commercial capture endpoints; separately verify eligible ready private/external progress while another provider is held.
- [ ] #4 Same AOT release supports ordinary local forwarding and direct configured provider. Operator rollback and account disable are tested.
- [ ] #5 Legacy and new commercial paths obey the same policy/admission/spend guard; rollback disables or migrates legacy routes without bypass.
- [ ] #6 Actual enablement is limited to approved accounts, models, ceiling and expiry; a declined/fixture-only decision cannot unlock live calls.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use already completed synthetic guard tests then qualify only an approved direct authenticated client. Pi release separately needs TASK-209/186/188. External trust/credential protection is supplied by deployment; AIR verifies privilege. TASK-217/218 refusal or absent approval keeps live work parked, not Done. Verify rollback and actual entitlement without implicit account changes.
<!-- SECTION:PLAN:END -->
