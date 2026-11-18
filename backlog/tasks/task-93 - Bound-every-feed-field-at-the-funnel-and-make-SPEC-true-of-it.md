---
id: TASK-93
title: Bound every feed field at the funnel, and make SPEC true of it
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
updated_date: '2026-11-18T09:44:00Z'
labels:
  - wire
  - events
dependencies: []
priority: high
ordinal: 92000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
From a high-tier commit review of 610f0d8, every claim re-measured by this seat. The v0.12.37 work capped the event Model field at the ring's single append funnel and gave WireText two budgets, but the request path's own failure producer builds its reason by interpolating the caller's raw model id before the funnel sees anything (RouterCore.cs:644-647, :828-850), and the funnel caps Model and not Reason (Events/RouterEvent.cs:67-72). So SPEC 10 currently promises a bound that one producer does not honour, and a caller can still choose how much text a feed record carries. The review also reported engine names uncapped and cuts that can split a surrogate pair; this seat verified the first and not the second, and both are criteria here so the answer is measured either way. Distinct ids cut to the same head are indistinguishable in the feed, which is an operator problem, not a lookup problem: lookup matches whole.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every producer that appends to the feed has its reason text held to the stated budget: a pin posts a 405-character model id through the request path's failure producer and reads the stored reason at reporting length.
- [x] #2 SPEC 10 is true of every producer as written, or SPEC narrows the claim to the producers that honour it — either way, in the same commit.
- [x] #3 Engine and profile names reaching a feed field or a caller's error body sit at reporting length, matching the number SPEC states.
- [x] #4 A cut cannot split a surrogate pair, and the marker plus dropped-count text fits inside the reserve the cap claims.
- [x] #5 Two ids that become identical once cut stay distinguishable in a feed record — a bounded fingerprint or the ticket id — pinned by a two-id test.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Shipped at [64b9f94](https://github.com/ArborealML/Inference-Router/commit/64b9f94a677faf188b7f71c2e0fd7994ae7731c4) (SPEC v0.12.39), ceilings unchanged at 128/1600 as instructed. `EventRing.Append` owns `Model`, `Engine`, `ThreadId` and `Reason`; the emitter's second whole-reason cut is gone; the request path cuts identifiers before building a sentence; the filter cuts its own query values. The marker now carries an eight-hex FNV-1a fingerprint of the whole value, backs off a surrogate pair, and names its worst case (`MarkerReserve = 32`); `WireText.Head` states a budget as a head, so reported names keep 24 characters inside the 500-character list. The marker grew, so the identifier head is 96, not 104 — wire-visible content, stated in §10.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Verified by this seat at 64b9f94 by reading, not running: the funnel caps all four fields (`git show 64b9f94:src/…/Events/RouterEvent.cs`, Append); §10 states 128 total / 96 head / 32 reserve and the two rules; every `Bound`/`Head` budget in source is 128, 1600, 160, 200, 500 or 24+32 — none is config-driven, so the new throw on a head-less budget cannot fire from config. Pins exist by name: five in `WireTextBudgetTests`, plus the funnel pin and the 405-character request-path pin. The two-id pin proves equal heads, unequal records and unequal fingerprints; it cannot prove absence of fingerprint collisions, and §10 says identifiers sharing a head "stay distinguishable" on a fingerprint folded to 32 bits — this seat reads that as stronger than the mechanism, and it is in front of the commit review now in flight. The coding seat reports 206 passed / 0 failed locally; CI attempt 1 of run 44277482601 went red 1 of 206 on the known shutdown pin and the rerun of that run is green, which is the gate.
<!-- SECTION:NOTES:END -->
