---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T07:55:00Z'
labels:
  - serve
dependencies: []
priority: high
ordinal: 84000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Blocked on @claire for one answer: which credential the router may use. The only inference engine listening on this box is strata on 18021 — the engine carrying live pi lanes — and it demands a secret that exists only in the owner's own engine-start tree; no shipped sample names a key path that exists here, and a secret a seat invents is refused by the engine. Either name a key file the router may read, or name an unauthenticated engine to front. Everything else on this row is measured or unblocked: the front-only path — model list, one completion, a stream to the sentinel, one refusal in the single lowercase shape — already works off a live process. Engine lifecycle stays a human act (shared-infrastructure mutation, safety section of AGENTS.md), and the owner has said not to optimize the local router.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The router serves a completion on a loopback address on this machine, one model resident, inside the configured 4-lane strata ceiling.
- [ ] #2 A long streamed completion traverses the router with no client-visible stall and no dropped frame, measured live rather than only in unit tests.
- [ ] #3 One static pi turn completes end-to-end through the router from a real pi session; nothing beyond Authorization and Content-Type reaches the backend.
- [ ] #4 The bring-up config names auto_switch and wait_health explicitly while TASK-61/62/64/25 are open, so serving is never read as swapping being safe.
- [ ] #5 No engine is started, stopped or reconfigured on this box without the owner's go-ahead recorded in this row.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Grant, provisional, relayed by the clerk at 07:06Z from the owner: the router may run on this box and may start, stop or reconfigure engines except the engine currently carrying live pi lanes; `auto_switch` stays off until the switching safety rows pass; on any anomaly, stop and leave it as found.** The same relay carries a standing constraint: pi's own lanes run on this box's strata config with one resident model, so swapping that resident model cuts the lanes this board, the implementer seat and the clerk are running on — proof goes on a separate engine or port. A veto overrides it; none has arrived.

**Blocked on @claire for one answer.** The only inference engine listening here is strata on 18021 — the engine carrying live pi lanes — and it demands a key at a path both samples name that does not exist here. A secret invented here is refused: strata compares it against the one its own start script generated. So name a key file the router may read, or point it at an unauthenticated engine. AC #3's pi swap cannot be proven on strata at all without cutting those lanes.

**Measured 07:52Z on a front-only probe** (auto_switch off, no lifecycle block, loopback only, an already-running OpenAI-shaped fake upstream; nothing started, nothing stopped): the router listed the model on `/v1/models`, passed one completion, relayed a stream to the sentinel, and answered an unknown model in the single lowercase error shape. Every criterion but a real engine and a real pi turn is now proven off a live process. Neither shipped sample is front-only as written — both set `auto_switch = true`, which is what AC #4 asks to see named. A benchmark fake backend on a loopback port is not the router; nobody has touched it.
<!-- SECTION:NOTES:END -->
