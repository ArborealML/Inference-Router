---
id: TASK-3
title: Measure router overhead for the SPEC throughput claim
status: Done
assignee: []
created_date: '2026-11-16T20:40:00Z'
labels: []
dependencies: []
ordinal: 3000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The spec claims request-path overhead is negligible but it is unmeasured. Benchmark router-fronted requests against direct engine calls at several concurrency levels and record the numbers the claim needs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A repeatable benchmark harness lives in the repo with documented run instructions
- [x] #2 Measured p50/p95/p99 overhead at 1, 4, and 16 concurrent streams
- [x] #3 SPEC section states the measured numbers instead of an unmeasured claim
<!-- AC:END -->

## Result (verified 2026-11-16)

Harness: `bench/Arboreal.InferenceRouter.Benchmarks` (JIT Release, one shared in-process fake
engine per layer; `dotnet run -c Release --project bench/... -- 400 60`). Numbers, exclusions and
the concurrency-16 HTTP cliff are recorded in `docs/benchmarks.md`; SPEC 9.1 quotes them.

Measured deltas (p50, allocations/request): routing +58.0 us / 15,448 B (conc 1), +90.6 / 15,144
(conc 4), +91.4 / 15,120 (conc 16); full HTTP path +217.1 / 26,056, +320.7 / 28,639, +995.8 /
31,523; SSE relay +58.1 / 24,956, +97.5 / 24,339, +102.3 / 24,097.

Found by the harness and fixed: the SSE relay polled every 200 ms for the ticket's stream
channel, so a stream that answered immediately still stalled up to a poll interval before its
first frame. The channel is now published at admission (before `Executing`) and
`RouterTicket.StreamReady` resolves it; the HTTP relay awaits it and sends keepalives every 4 s.
Regression tests: `ServerWireTests.Streaming_With_Immediate_Engine_Reply_Never_Loses_Frames`,
`EndToEndTests.Streaming_Channel_Is_Published_With_Executing_State`. 66/66 tests green, build and
AOT publish clean.
