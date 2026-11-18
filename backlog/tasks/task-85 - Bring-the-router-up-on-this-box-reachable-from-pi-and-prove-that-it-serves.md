---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T07:16:00Z'
labels:
  - serve
dependencies: []
priority: high
ordinal: 84000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Blocked on @claire for one grant: may a seat start or stop an inference engine on this machine, or does it front an endpoint the owner already runs? Nothing else on this row is waiting. The owner is tuning the engine underneath this box and has said not to optimize the local router, so engine lifecycle here is shared-infrastructure mutation and stays a human act (safety section of AGENTS.md). What is unblocked: the router fronting an already-running engine and proving serving, streaming and one real pi turn end to end.
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
**Provisional grant, relayed by the clerk at 07:06Z from the owner verbatim: "I'd really like to wake up to the inference router running successfully and Pi able to swap between models with /model." Grant text: "The owner wants the router running by morning. The router may run on this box and may start, stop or reconfigure engines **except the engine currently carrying live pi lanes**; `auto_switch` stays off until the switching safety rows pass; on any anomaly, stop and leave it as found." The owner may veto; if nothing arrives by 08:00Z this stands as provisional, not final.

**Hard constraint from the same relay, standing:** pi's own lanes are served by this box's strata config on one resident model. Swapping that resident model overnight cuts the lanes this board, the implementer seat and the clerk run on. Proof goes on a separate engine or port; nobody touches the live one without a named window and the owner's nod.

**The box, re-measured at 07:08Z** (the clerk was right that no router listens): nothing on 8321 or 18080; 18021 answers 401 `authentication_error` — a live engine wanting a key; 18020 refuses connection, though both shipped samples name that endpoint; 18044 is the session-manager bridge; 18081 the backlog server; 18082 a tailnet forwarder for the docs page, not the API. One extra finding: a **benchmark fake backend is still listening** — `bench/.../Benchmarks 2000 200`, pid 716436, answering `/v1/models` with `bench-model` on 127.0.0.1:37541. It is not the router and stays running until the owner says otherwise.

**Config note:** both shipped samples set `auto_switch = true`, so neither is front-only as written; the grant requires it off, which is what AC #4 asks to see named.
<!-- SECTION:NOTES:END -->
