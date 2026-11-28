---
id: TASK-207
title: "Persist authenticated owned inference thread policy"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-204
priority: high
ordinal: 207000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Introduce a minimal single-writer durable policy record: owner, thread, lineage, sensitivity and revision. New authorized roots/stateless work default insensitive; agent-bound sessions cannot mint or omit identities to bypass inherited policy. Sensitivity filters trusted private routes only.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A new authorized root defaults insensitive; existing unknown, missing or corrupt policy fails closed, including restore of stale state.
- [ ] #2 Owner-scoped identity is enforced on ingress, status, cancel and events. Equal raw IDs from distinct principals never share thread state.
- [ ] #3 Derived/restored sessions inherit an owned policy group; forged or omitted identity using an agent-bound credential is refused.
- [ ] #4 Provider allow/deny restrictions intersect sensitivity and capabilities, deny wins, malformed/empty/unknown inputs cannot broaden eligibility.
- [ ] #5 Queued dispatch, count/tokenize, retry, fallback and reload revalidate policy, trust and frozen request restrictions. Sensitive work never reaches commercial capture endpoints.
- [ ] #6 Durable restart/backup and scope tests preserve policy without storing prompt bodies. SPEC §§5–7, 10, 13–14 document migration and authorization.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one small versioned policy store and scoped ingress credentials; no identity service or general policy engine. Keep owner-only declassification for TASK-208. Define trust as explicit private control, not transport or ZDR. Strip internal headers before upstream submission.
<!-- SECTION:PLAN:END -->
