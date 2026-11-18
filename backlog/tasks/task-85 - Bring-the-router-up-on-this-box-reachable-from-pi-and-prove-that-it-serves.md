---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T20:08:00Z'
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
Wake on a backend-side header capture for the pi inference request. The deployed front-only router serves the resident model. The owner parked engine startup, shutdown and reconfiguration on this host. A residency swap requires a separately authorized host or window.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The router serves a completion on a loopback address on this machine, one model resident, inside the configured 4-lane strata ceiling.
- [x] #2 A long streamed completion traverses the router with no client-visible stall and no dropped frame, measured live rather than only in unit tests.
- [ ] #3 One static pi turn completes end-to-end through the router from a real pi session; nothing beyond Authorization and Content-Type reaches the backend.
- [x] #4 The bring-up config names auto_switch and wait_health explicitly while TASK-61/62/64/25 are open, so serving is never read as swapping being safe.
- [x] #5 No engine is started, stopped or reconfigured on this box without the owner's go-ahead recorded in this row.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Deployed [e07296d](https://github.com/ArborealML/Inference-Router/commit/e07296d) after a canary reported the resident model. The coordinator ran the complete Release suite, 248 passed. One live stream returned HTTP 200 in 7.505 seconds. All 360 JSON chunks parsed, two router keepalives were SSE comments, and the assembled text contained each integer from 1 through 100 in order followed by finish_reason stop and [DONE]. A real ephemeral pi turn using the configured provider returned AIR_LIVE_OK. The upstream header set for that turn remains unmeasured. The config explicitly disables auto_switch and documents why wait_health is inapplicable to a remote engine without a lifecycle provider. Configured admission is two requests under a four-slot ceiling. Earlier testing on d0240c2 also observed a catalog-name request exceeding its configured wait, tracked in TASK-51.
<!-- SECTION:NOTES:END -->
