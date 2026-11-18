---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T17:46:00Z'
labels:
  - serve
dependencies: []
priority: high
ordinal: 84000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Wake:** two proofs are still open — one long streamed completion through the router with no client-visible stall or dropped frame, and one static pi turn from a real pi session — and TASK-108 gates both for any strict OpenAI client, because today's keepalive arrives as a `data:` payload. The credential question that blocked this row is answered: the bring-up config sits outside the repository, written at the owner's instruction (its own header records that), and names `auto_switch = false`, `remote = true` and no lifecycle block, so nothing on this row starts, stops or reconfigures an engine. Engine lifecycle stays a human act (AGENTS.md safety), and the running instance fronts the engine carrying live pi lanes without managing it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The router serves a completion on a loopback address on this machine, one model resident, inside the configured 4-lane strata ceiling.
- [ ] #2 A long streamed completion traverses the router with no client-visible stall and no dropped frame, measured live rather than only in unit tests.
- [ ] #3 One static pi turn completes end-to-end through the router from a real pi session; nothing beyond Authorization and Content-Type reaches the backend.
- [ ] #4 The bring-up config names auto_switch and wait_health explicitly while TASK-61/62/64/25 are open, so serving is never read as swapping being safe.
- [x] #5 No engine is started, stopped or reconfigured on this box without the owner's go-ahead recorded in this row.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Grant, provisional, relayed 07:06Z, and SUPERSEDED for engine lifecycle at 17:55Z:** the router may run on this box; starting, stopping or reconfiguring an engine is **parked on this host** (no transaction, no window), and the engine carrying live pi lanes is untouched either way; `auto_switch` stays off until the switching safety rows pass; on any anomaly, stop and leave it as found. Standing constraint from the same relay: pi's own lanes run on this box's engine config with one resident model, so swapping that resident model cuts the lanes this board, the implementer seat and the clerk run on — proof goes on a separate engine or port. A veto overrides it; none has arrived.

**Measured 07:52Z on a front-only probe** (auto_switch off, no lifecycle block, loopback only, an already-running OpenAI-shaped fake upstream): the router listed the model, passed one completion, relayed a stream to the sentinel, and answered an unknown model in the single lowercase error shape. Neither shipped sample is front-only as written — both set `auto_switch = true`, which is what #4 asks to see named.

**Measured by this seat at 17:33Z on the running instance** (a build of `d0240c2`, not current main): `/healthz` 200; `/v1/models` lists exactly one model, `owned_by` the engine it fronts; one non-streamed completion returned 200 in 2.3 s with usage counts; an unknown model name answered 503 `model_unavailable` in the single lowercase shape. The config's own read names `max_inflight_per_engine = 2` under a 4-slot ceiling and no lifecycle block, so #1 and #5 are met on this seat's reads. #2, #3, #4 stay open — #4 because the config names `auto_switch` but not `wait_health`. One defect measured here belongs to TASK-51: a request naming the catalog name the engine does not report loaded answered nothing in 90 s, past `switch_wait_timeout_ms = 60000`.
<!-- SECTION:NOTES:END -->
