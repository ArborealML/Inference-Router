---
id: TASK-183
title: Let AIR choose the serving profile and scale concurrency from queue demand
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:49:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-22
dependencies:
  - TASK-203
priority: high
ordinal: 175000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add strict numeric context-floor and independent fit/admission reporting without breaking existing explicit named-profile semantics. Client names model and optional hard floor; AIR chooses suitable qualified recipe for numeric mode. Elective optimization is separate.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A fitting verified resident serves without relaunch; busy preferred route cannot hide other eligible ready alternatives.
- [ ] #2 Candidate floor is max(client minimum, rendered input plus enforced output/reasoning and reserve); reject malformed/repeated/overflow/conflicting fields and qualify exact or conservative counts.
- [ ] #3 Additive numeric API preserves documented explicit legacy profile semantics; ambiguous exact recipe refuses. No legacy header retirement before supported installed client migration.
- [ ] #4 Report context, physical lanes, admission target, active/reserved and available capacity separately; admission target does not reclaim physical KV allocation.
- [ ] #5 Busy compatible resident does not force context escalation; compare eligible alternatives or wait under independent policy.
- [ ] #6 Numeric-mode fixtures preserve existing recipe-bound conformance and never grant unqualified lanes; actual new launch enablement waits for the applicable host/conformance contracts.
- [ ] #7 SPEC §§5–7, 10, 14 pin fit and compatibility; capture fixtures exercise local-only count/tokenize without external disclosure.
- [ ] #8 Existing catalog/status/CLI/monitor distinguish required and allocated context, physical lanes, admission target and actual occupancy using shared bounded read models.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Land strict ingress normalization and shared candidate fit independently of elective resize. Count by backend/render identity; use trusted-local or synthetic counting until integrated external authorization/accounting exists. Preserve existing named-client behavior and current safe launch settings. No silent truncation or automatic larger-lane tie-break. TASK-184 migrates Pi; retain supported legacy header unless a later explicit decision retires it.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Previous review confirmed name matching and declaration-order tie-break. Revision 5 reconciliation, with the accepted corrective review amendments, supersedes wider-lane preference and contraction non-goal. Refresh plan review; TASK-190/197/198 retain transition authority.
<!-- SECTION:NOTES:END -->
