---
id: TASK-30
title: >-
  Bound the relay against a client that stops reading, and a generation that
  stalls after its first frame
status: Preparing
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-17T07:01:00Z'
updated_date: '2026-11-20T13:35:00Z'
labels:
  - stream
  - transport
milestone: m-1
dependencies:
  - TASK-34
priority: high
ordinal: 29000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Delivery is checking the archived traces for missing watchdog timing, ordering, one-byte writes and HTTP/2 idle evidence. Both limits remain opt-in. A repeat run requires separate release.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A stream whose downstream reader stops consuming ends within a stated bound, with the engine slot released and a named error code on the feed
- [x] #2 Buffering held for one stalled reader is bounded by a number that is in config, not a constant, and the bound's default is stated in SPEC - the number and whether it counts frames or bytes come from TASK-34
- [x] #3 A generation silent past the stated idle bound after its first frame ends in band with SPEC 6's committed-stream shape: one error frame plus `[DONE]`, no new truncation format
- [x] #4 The bound and its code name come from TASK-34's adopted values; the behaviour is pinned by `RequestPhaseTimeoutTests` and named in the SPEC 14.1 row
- [ ] #5 SPEC 14.1 row 2 no longer lists this as open, and a test pins each of 1-3 without a real engine
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Build against ba3c1cc. Exercise a stopped HTTP/1.1 reader and an HTTP/2 stream withholding credit alongside a healthy sibling. Use five distinct thread headers. Show the fifth request starts after the selected stalled ticket retires while three other requests remain active. Compare wrapped and unwrapped diagnostics under matching protocol and rate settings. Verify setup in a separate untimed localhost preflight. The timed plan budgets 750 seconds per protocol and requires a separately granted exclusive window.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [opt-in relay](https://github.com/ArborealML/Inference-Router/pull/45) passed 39 focused and 54 neighboring Release cases plus warning-free Native AOT. The [credit-release fixture correction](https://github.com/ArborealML/Inference-Router/pull/46) passed five focused rows and [hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44683761424).

The exclusive November 20 run accounted for all 50 rows: 35 passed, one parity result, seven unproven and seven skipped. All ten stock saturation trials reported slow_consumer with causal admission of the fifth request. Full watchdog timing and ordering were demonstrated in two of five HTTP/1.1 and four of five HTTP/2 trials. Resume and caller abort passed on both protocols. The attempted one-byte writes were 49,152 and 4,096 bytes. HTTP/1.1 short-idle controls passed; the HTTP/2 pair was incomplete. The maximum observed relay credit was 262,144 bytes per ticket. The series completed without invalidation; raw traces and checksums are retained outside the repository.

The soak criterion remains open. Representative replay still requires per-engine monotonic raw-read gaps with pause attribution, workload counts, completeness and provenance; the earlier inventory found zero replay-usable streams.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
