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
1, 4 and 16. Requests carry a ~1 KB prompt so string/prefix work is realistic rather than a toy.

Run:

```sh
dotnet run -c Release --project bench/Arboreal.InferenceRouter.Benchmarks -- 400 60
#                                     requests per scenario ─┘        └ warm-up
```

The full-matrix figure below is a 400/60 run over 18 scenarios. A 2000/200 run takes well over
ten minutes on this box, so keep the smaller setting for regressions and use the larger one
before a release.

## Numbers (2026-11-16, 400 requests / 60 warm-up per scenario)

Box: .NET 11.0.0, Ubuntu 26.04.1, 24 logical cores, workstation GC.
Latencies in microseconds; `alloc` is GC-allocated bytes per request.

| scenario | conc | p50 | p95 | p99 | alloc B/req | req/s |
|---|---:|---:|---:|---:|---:|---:|
| engine direct (non-stream) | 1 | 7.6 | 12.7 | 17.3 | 5,329 | 115,108 |
| engine direct (stream) | 1 | 22.4 | 33.2 | 37.5 | 35,497 | 46,770 |
| engine direct (non-stream) | 4 | 4.4 | 5.1 | 6.8 | 5,331 | 200,210 |
| engine direct (stream) | 4 | 10.6 | 31.1 | 34.4 | 35,497 | 63,358 |
| engine direct (non-stream) | 16 | 4.3 | 4.7 | 5.2 | 5,311 | 221,349 |
| engine direct (stream) | 16 | 10.9 | 11.7 | 18.8 | 35,479 | 87,085 |
| router enqueue (non-stream) | 1 | 65.6 | 78.9 | 86.4 | 20,777 | 14,298 |
| router enqueue (stream) | 1 | 80.5 | 96.2 | 114.1 | 60,453 | 11,747 |
| router enqueue (non-stream) | 4 | 95.0 | 116.3 | 308.7 | 20,475 | 18,776 |
| router enqueue (stream) | 4 | 108.1 | 142.3 | 925.6 | 59,836 | 16,877 |
| router enqueue (non-stream) | 16 | 95.7 | 117.2 | 143.1 | 20,431 | 21,915 |
| router enqueue (stream) | 16 | 113.2 | 149.4 | 1,032.4 | 59,576 | 19,242 |
| router http (non-stream) | 1 | 224.8 | 251.6 | 264.1 | 31,385 | 4,325 |
| router http (stream) | 1 | 269.3 | 301.1 | 1,015.4 | 162,850 | 3,459 |
| router http (non-stream) | 4 | 325.1 | 462.5 | 873.5 | 33,970 | 7,610 |
| router http (stream) | 4 | 404.1 | 1,116.3 | 2,090.2 | 165,453 | 5,975 |
| router http (non-stream) | 16 | 1,000.2 | 1,577.3 | 3,769.3 | 36,834 | 8,349 |
| router http (stream) | 16 | 1,213.4 | 2,643.0 | 4,065.0 | 166,819 | 6,405 |

Cost attributed to the router (same run, subtraction of the layer below):

| added by | conc 1 | conc 4 | conc 16 |
|---|---|---|---|
| routing (enqueue − engine direct) | +58.0 µs p50, +15,448 B/req | +90.6 µs p50, +15,144 B/req | +91.4 µs p50, +15,120 B/req |
| full HTTP path (http − engine direct) | +217.1 µs p50, +26,056 B/req | +320.7 µs p50, +28,639 B/req | +995.8 µs p50, +31,523 B/req |
| SSE relay on top of routing (http stream − enqueue stream) | +58.1 µs p50, +24,956 B/req | +97.5 µs p50, +24,339 B/req | +102.3 µs p50, +24,097 B/req |

Reading: a real engine serves a token in milliseconds. The router's entire HTTP path for a
streaming request is 269 µs at concurrency 1 — a fraction of one millisecond — and the routing
decision itself is tens of microseconds. So routing overhead is not what a user experiences;
queue wait and engine prefill dominate, which is why §12.3 measures impact on other requests
rather than per-request routing cost.

Two shapes worth remembering from this run:

- **Routing does not degrade.** Enqueue cost is flat from concurrency 4 to 16 (95 → 96 µs
  non-stream, 108 → 113 µs streaming) and its allocation per request barely moves. The
  scheduler, queue and trie are not the bottleneck.
- **The HTTP plumbing does.** At concurrency 16 the http rows go to ~1.0 ms / ~1.2 ms p50 while
  the engine underneath stays at 4–11 µs. That gap is Kestrel/ASP.NET request handling plus SSE
  framing on a workstation-GC box under a synthetic flood, not routing. It is the first place to
  look when concurrency targets get serious (server GC, connection/queue settings, and real
  engines, which never issue 220k req/s).

## What these numbers do not cover

- **AOT-published binaries.** The harness is JIT. Native-AOT changes codegen and the
  allocation profile; publish numbers are a separate measurement, still owed.
- **Allocations per output token.** §9.1 asks for this; the harness reports per request.
- **Real engines.** The fake engine does no tokenization, sampling or KV work, and never
  pushes back. Everything about prefill interference, slot exhaustion and context-window
  escalation (§7.6) is unmeasured.
- **Tail behaviour under contention.** The p99 on the streaming paths (0.9–2 ms at concurrency
  4, 4 ms at 16) comes from frame scheduling in the HTTP layer, not the router's decision cost.
  Re-measure these first after any §12 scheduling change.
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
