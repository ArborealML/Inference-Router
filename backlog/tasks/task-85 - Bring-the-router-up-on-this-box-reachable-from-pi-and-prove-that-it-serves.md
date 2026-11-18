---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T06:09:00Z'
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
Pairing: AC #2 is the live half of TASK-82 (frame preservation at any split byte) and TASK-30 (a relay against a client that stops reading); a green unit suite is not evidence for this row. AC #3 needs only what is already true in code — the router forwards exactly Authorization and Content-Type upstream (`RouterCore.cs:670-681`), so no router-side header reaches a backend.

Config trap, re-measured: `Remote = e.Remote ?? false` at `RouterConfigLoader.cs:286`, and `samples/router.toml`'s hyperqwen entry carries neither `remote = true` nor a lifecycle block — it lands on the router-owns-it side with nothing to run. A bring-up config either names `remote = true` for every endpoint it does not own or states the lifecycle command it expects; `samples/router.safe-smoke.toml` is the only sample safe to smoke-test.
<!-- SECTION:NOTES:END -->
