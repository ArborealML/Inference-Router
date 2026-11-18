---
id: TASK-76
title: >-
  Withdraw health only when the /models fetch failed, and never over a switch's
  own proof
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T02:36:00Z'
updated_date: '2026-11-18T21:15:00Z'
labels:
  - concurrency
milestone: m-2
dependencies:
  - TASK-67
priority: medium
ordinal: 76000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on the remaining refusal-path qualification under TASK-77. The models-fetch failure boundary, marker protection and failed-switch intermediate provenance are integrated. TASK-68 can proceed on the verified marker criterion.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A fault raised anywhere in the health round except the /models fetch cannot publish Healthy=false, pinned with an injected throwing seam
- [x] #2 A poll that starts after a switch published its proof and then fails before the marker clears leaves the proven snapshot healthy
- [ ] #3 SPEC 5.5 names the one observation that moves Healthy, and both new pins fail before the change and pass after it, with the runs named
- [x] #4 The failed-exit pin asserts the snapshot between the unknown publication and the marker clear, with an exact version check, so leaving the provenance bit set at the exit fails a pin.
- [ ] #5 A preflight refusal before mutation preserves the incumbent’s valid proof and permits a subsequent inference on it. An uncertain mutating operation withdraws readiness pending fresh proof.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [combined implementation](https://github.com/ArborealML/Inference-Router/commit/fcff3ea) passed 90 integrated cases, 421 full Release tests and server Native AOT. A broad exception filter initially missed a late HTTP 503 when the polling loop was cancelled. The corrected code preserves that evidence. On the unfixed parent, an injected pre-fetch fault failed the same-snapshot assertion. On the final candidate, three isolated mutations each failed its intended assertion: pre-fetch snapshot, marker-held snapshot and intermediate claim provenance. Restoring each mutation passed its case. The marker protection predated this change, so its regression evidence is a deliberate reversal. Returned preflight refusals and attempted mutations passed; thrown read-only operations and subscriber failures remain with TASK-77.
<!-- SECTION:NOTES:END -->
