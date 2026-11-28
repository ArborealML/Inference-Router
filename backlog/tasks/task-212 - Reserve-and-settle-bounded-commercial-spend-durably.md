---
id: TASK-212
title: "Reserve and settle bounded commercial spend durably"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-210
  - TASK-211
priority: high
ordinal: 212000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement a single-writer attempt/spend store before any paid route is enabled. Distinguish admission estimate, actual usage, actual charge and unresolved exposure. Do not persist prompt content by default.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Before possible send, persist unique attempt/body digest, account/config/policy revision, bounded charge and budget period; reservation and concurrent admission cannot double spend.
- [ ] #2 Hard budget requires conservative billable input, aggregate output/reasoning/choice bounds, maximum applicable cache/other prices and bounded fees; unknown bounds refuse.
- [ ] #3 Crash before/after send, partial usage, cancel, reload/removal and reset retain unresolved liability; missing evidence is never zero.
- [ ] #4 Reconcile authoritative evidence or audited manual adjustment. Unresolved commitments survive backup/restore and cannot expire by retention timer.
- [ ] #5 No automatic replay after uncertain submission; an explicitly permitted independent replacement reserves separately and never splices semantic output.
- [ ] #6 Tests inject failures at journal/send/settle boundaries and prove account-local quarantine, no unrelated-provider stall, and unchanged AOT. SPEC §§5–7, 10, 14 records guarantees.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use durable atomic replacement or append/flush helpers already proven in the repository, not the launch journal state machine. Conservative unresolved bounds count against budget; actual evidence can release only proven surplus. Keep request lookup optional and correlation distinct from idempotency.
<!-- SECTION:PLAN:END -->
