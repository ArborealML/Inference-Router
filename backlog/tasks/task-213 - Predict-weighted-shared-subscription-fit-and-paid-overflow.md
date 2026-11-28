---
id: TASK-213
title: "Predict weighted shared subscription fit and paid overflow"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-210
  - TASK-211
  - TASK-212
priority: high
ordinal: 213000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Add typed subscription windows and fit prediction for DeepRelay using documented weighted input/output and weekly/monthly shared allowance, backstop and enabled fees. Included and paid can be dispositions of one account/request, not independent routes.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Reserve conservative weighted bounds across all applicable windows and simultaneous account routes; pre-request observations account for overlap without granting stale headroom.
- [ ] #2 Handle documented factor omission, fixed-point scaling, reset timestamps, missing versus zero and separate cached/reasoning/fee semantics.
- [ ] #3 Model-only preflight remains advisory and never reserves capacity or guarantees included-only operation.
- [ ] #4 Predicted included, actual included, metered, included-plus-fees and unknown are distinct; authoritative log correlation can settle evidence but does not deduplicate.
- [ ] #5 When predicted fit fails, select another permitted eligible route or wait/refuse. Paid overflow requires explicit permission and bounded exposure independent of queue priority.
- [ ] #6 Synthetic fixtures demonstrate reset/race/missing-log and out-of-order cases without paid requests. SPEC §§5–7, 10, 14 documents no-overage limitation.
- [ ] #7 When otherwise eligible routes are ready, prefer suitable included capacity per configured policy; paid fallthrough reserves its worst-case monetary exposure even when predicted included.
- [ ] #8 Weighted-window and worst-case monetary commitments share the same atomic attempt reservation; force included-fit race loss and verify permitted alternate, explicit overflow, or scoped denial without duplicate commitments.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend TASK-211's deterministic route evaluator with explicit included preference, weighted weekly/monthly typed fields and shared account reservations. Fit is prediction only. If no-overage is unverified and paid fallthrough unauthorized, refuse or use another eligible route. Use bounded configured telemetry; expose predicted versus observed disposition, window/reset/freshness via existing status/events and CLI/monitor. No universal meter or vendor DSL.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Basic metered delivery is not dependent on this task.
<!-- SECTION:NOTES:END -->
