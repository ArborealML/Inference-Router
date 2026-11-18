---
id: TASK-93
title: Bound every feed field at the funnel, and make SPEC true of it
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
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
- [ ] #1 Every producer that appends to the feed has its reason text held to the stated budget: a pin posts a 405-character model id through the request path's failure producer and reads the stored reason at reporting length.
- [ ] #2 SPEC 10 is true of every producer as written, or SPEC narrows the claim to the producers that honour it — either way, in the same commit.
- [ ] #3 Engine and profile names reaching a feed field or a caller's error body sit at reporting length, matching the number SPEC states.
- [ ] #4 A cut cannot split a surrogate pair, and the marker plus dropped-count text fits inside the reserve the cap claims.
- [ ] #5 Two ids that become identical once cut stay distinguishable in a feed record — a bounded fingerprint or the ticket id — pinned by a two-id test.
<!-- AC:END -->
