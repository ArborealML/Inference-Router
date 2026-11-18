---
id: TASK-85
title: Bring the router up on this box, reachable from pi, and prove that it serves
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T06:06:00Z'
updated_date: '2026-11-18T20:27:00Z'
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
Qualified a forwarding-only deployed router with real pi inference and a long, complete stream. The shared engine remains outside router lifecycle management.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The router serves a completion on a loopback address on this machine, one model resident, inside the configured 4-lane strata ceiling.
- [x] #2 A live stream completes within the 30-second stall bound. Its reconstructed payload contains every requested integer in order, with one terminal stop and one [DONE] sentinel.
- [x] #3 One static pi turn completes end-to-end through the router from a real pi session; nothing beyond Authorization and Content-Type reaches the backend.
- [x] #4 The bring-up config names auto_switch and explicitly documents the wait_health posture. For a forwarding-only engine with no lifecycle provider, state that wait_health is inapplicable.
- [x] #5 No engine is started, stopped or reconfigured on this box without the owner's go-ahead recorded in this row.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Deployed [e07296d](https://github.com/ArborealML/Inference-Router/commit/e07296d) after a canary reported the resident model. The coordinator ran the complete Release suite, 248 passed. One live stream returned HTTP 200 in 7.505 seconds. All 360 JSON chunks parsed, two router keepalives were SSE comments, and the assembled text contained each integer from 1 through 100 in order followed by finish_reason stop and [DONE]. A real ephemeral pi turn using the configured provider returned AIR_LIVE_OK. The config explicitly disables auto_switch and documents why wait_health is inapplicable to a remote engine without a lifecycle provider. Configured admission is two requests under a four-slot ceiling. Earlier testing on d0240c2 also observed a catalog-name request exceeding its configured wait, tracked in TASK-51.

At [31179e3](https://github.com/ArborealML/Inference-Router/commit/31179e3), a dedicated TCP observer recorded only header names on the deployed router to peer connection during the successful interactive pi turn. The request carried Host, Content-Length and Content-Type. The peer required no authorization. No pi routing or scheduling header reached it. The earlier bounded long stream completed in 7.505 seconds overall with a 30-second low-speed abort and exact reconstructed output.
<!-- SECTION:NOTES:END -->
