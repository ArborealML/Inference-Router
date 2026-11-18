---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels:
  - serve
milestone: m-0
dependencies:
  - TASK-108
priority: high
ordinal: 84000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake after an authorized deployment of the streaming fix. The running front-only router was reported at d0240c2 on 2026-11-18; 4a5e214 changes its queued keepalive wire format. Qualify the deployed revision with a long stream and a static pi turn. An isolated listener can qualify the build separately. The private config sets auto_switch=false and remote=true. Record the explicit wait_health posture at the authorized deployment. Engine lifecycle on this host remains parked.
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
The owner parked engine startup, shutdown and reconfiguration on this host at 17:55Z. The earlier permission was superseded. Only front-only serving is authorized here. TASK-112 requires an authorized isolated host or a newly granted window for a lifecycle transaction.

**Measured 07:52Z on a front-only probe** (auto_switch off, no lifecycle block, loopback only, an already-running OpenAI-shaped fake upstream): the router listed the model, passed one completion, relayed a stream to the sentinel, and answered an unknown model in the single lowercase error shape. This measures the early probe configuration.

**Measured by this seat at 17:33Z on the running instance** (a build of `d0240c2`, not current main): `/healthz` 200; `/v1/models` lists exactly one model, `owned_by` the engine it fronts; one non-streamed completion returned 200 in 2.3 s with usage counts; an unknown model name answered 503 `model_unavailable` in the single lowercase shape. The config's own read names `max_inflight_per_engine = 2` under a 4-slot ceiling and no lifecycle block, so #1 and #5 are met on this seat's reads. #2, #3, #4 stay open — #4 because the config names `auto_switch` but not `wait_health`. One defect measured here belongs to TASK-51: a request naming the catalog name the engine does not report loaded answered nothing in 90 s, past `switch_wait_timeout_ms = 60000`.

At 5c5e18a, ConfigDto.cs defines wait_health on DockerDto and SelectDto only. For a remote front-only engine with neither block, document that readiness waiting is inapplicable in the local config alongside auto_switch=false. Do not add a lifecycle provider solely to carry this setting. Verify the actual configuration at deployment before checking #4.
<!-- SECTION:NOTES:END -->
