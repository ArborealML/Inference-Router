---
id: TASK-10
title: Harden the backend to PAIR parity where the comparison finds gaps
status: To Do
assignee: []
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-16T20:46:00Z'
labels:
  - backend
milestone: v0.10 - live view and robustness parity
dependencies:
  - TASK-9
ordinal: 10000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The external comparison against NVIDIA Personal-AI-Router (TASK-9) concluded this router is
**not yet as robust as PAIR, even single-host**, while already leading on priority, admission,
serving profiles and prefix policy. This task closes the robustness gap in the recommended order:
URI handling + readiness proof + dispatch progress, then transport bounds and phase timeouts, then
switch serialization and the error contract, then owned lifecycle/drain/recovery (delivered by
TASK-1), then security and tests. Durability, fairness and version pins follow.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
## Plan (reordered after the deepest tier plan review; each step ships with fake-backed tests per AC #8)

Review source: the research service deepest tier plan review, mirrored at `.local/reviews/r13-task10-plan-solpro.md`
(untrusted external output; the claims below were re-checked against the code by me).

Facts this plan is written against (verified at 329970a):
- One flat `request_timeout_seconds` per engine, with an infinite `HttpClient` timeout so long streams
  survive (`EngineRuntime.cs:67`). No phase deadlines exist.
- Admission **reserves engine capacity at selection** (`RouterCore.cs:284`) before the model-switch wait
  (`RouterCore.cs:375`), so a switch waiter holds a slot the whole time it waits. An earlier note in this
  plan claimed switch waiters were excluded from capacity since 8af6aac — that was wrong: 8af6aac changed
  SPEC and board text only, no code. Consequence: on a box where every engine is switching, admitted
  tickets consume all slots and AC #10 (prompt no-capacity error) cannot be met by tuning alone.
- `ticket.ExecCts` is created unlinked from the client token (`RouterCore.cs:355`), so a client that drops
  the connection does not stop upstream generation; the slot stays reserved until the engine finishes.
- The commit boundary is the **first flushed frame**, not the first delta: `StreamTicketAsync` writes status
  200 and a keepalive frame before any token reaches the client (`ServerApp.cs:172-181`), which is why
  "retry after the first token" is not the right line — the response is already started. Client receipt is
  unobservable; server commit is what we can know.
- Switch single-flight with per-target state (`RouterCore.ExecSwitchAsync`, `_switchState`); events through a
  bounded `EventRing` (`Events/RouterEvent.cs:33`); config parsed once at startup, no reload path at all.

0. **AC #1 — contracts and URI composition (mostly landed).** `models_endpoint` is configurable and pinned
   by samples + a loader test. Still missing: base endpoint + probe path join, trailing slash, query string,
   and relative `health_probe.path` resolution tests.
1. **AC #5 — the perimeter, before anything that promises bounds.** Authenticate before reading bodies
   (per-caller credentials from named env vars or key files, constant-time compare, `/admin/*` fail-closed),
   TLS at Kestrel or a trusted proxy, and ASP.NET Core's partitioned rate limiter keyed by credential with
   `QueueLimit = 0` so over-cap callers are rejected rather than queued. Keep the router's own key separate
   from each upstream engine's key and test that the engine key actually goes out on the wire. **Owner
   decision needed: the credential config shape** (`[[ingress.credential]]` name + `key_env`/`key_file`, or a
   single shared key with no per-identity caps). Also closes the escalation gap noted below.
2. **AC #3 (first half) + AC #2 — lifetime and transport.** Create the ticket CTS at enqueue, linked to
   `ctx.RequestAborted` and shutdown; give the ticket one atomic terminal transition; make the capacity lease
   a single object released exactly once in `finally`; keep switch waiters **outside** inference capacity.
   Then the bounds: `max_request_bytes` at ingress (Kestrel `MaxRequestBodySize` plus the reader), an upstream
   byte cap enforced by a counting relay over a bounded channel of owned pooled buffers, and phase deadlines —
   connect, response headers, first byte, inter-read idle — with idle timing paused while the client applies
   backpressure. Commit-boundary behaviour: pre-commit failure answers the OpenAI error body; post-commit
   failure emits `engine.stream_aborted`, closes the stream, and sends **no** `[DONE]` and no replay. That
   last part contradicts what `docs/ai-integration.md` and SPEC §6 describe as today's framing, so the same
   commit updates them.
3. **AC #4 — switch contract plus epochs.** Capture immutable `(target, epoch, typed completion)` state with
   owned deadlines; a waiter's cancellation only detaches that waiter. Coalesce requests for the same target
   and queue competitors; clear the latched state once; keep the failure latched until an explicit retry,
   config change or epoch change; reject completions from a stale epoch. Error casing: lowercase for `/v1`,
   PascalCase for `/admin` (pin it with a table test rather than "fixing" one of them).
4. **AC #3 (second half) + AC #10 — dispatch and aging.** Schedule the exact ticket the scheduler decided on
   instead of popping another head; wake dispatch on state changes plus one deadline timer rather than
   polling; bound how far an eligible lower-priority ticket can be bypassed (`N`) and pin it with a test.
   AC #10 then falls out: when no engine can serve, the request terminates on its own visible deadline with a
   no-capacity error instead of sitting in `WaitingForModel` forever.
5. **AC #7 — bounded telemetry.** Add a `StreamId` per subscriber, subscribe atomically after a cursor, bound
   each subscriber's queue and report the gap as `(requested, oldest, current, dropped)` with loss sources
   separated; support `Last-Event-ID` reconnect; add a metadata-only recent-request journal bounded by count
   and bytes.
6. **AC #6 — no-reload gap, decided.** Take the reviewer's accepted-gap wording verbatim (see
   `.local/reviews/r13-task10-plan-solpro.md` §8): no in-process configuration reload through v0.10; changes
   require restart; records expose a secret-redacted `ConfigPin`; requests stay snapshot-pinned. Engine crash:
   clearing readiness must also clear loaded models/gauges, cancel in-flight work as `engine_died`, wake
   dispatch, and advance a `ReadinessEpoch` once per lost→ready generation (`EngineEpoch` itself is TASK-1
   instance identity).

Two earlier claims in this plan were wrong and are corrected above: capacity reserved before the switch wait,
and AC #10 being done. AC #10 is open and belongs to step 4.

<!-- AC:BEGIN -->
- [ ] #1 URI/path composition tested (base endpoint + probe paths, trailing slash, query string, relative `health_probe.path` resolution)
- [ ] #2 Request bodies bounded; each phase (connect, headers, first token, idle stream) has a timeout; an explicit commit boundary decides whether in-flight work is recovered or declared lost
- [ ] #3 Cancellation reaches the engine and releases reserved capacity; a queued ticket can neither spin nor starve (aging bound pinned by a test)
- [ ] #4 Switches serialized and coalesced; every failure path resets state; the error contract (codes + payload shape) pinned by a table test
- [ ] #5 Ingress auth/TLS/rate caps and engine API keys enforced; resource identity fail-closed (unlabelled/unowned containers refused)
- [ ] #6 Config reload atomic with the config pin recorded; engine crash produces readiness cleanup and a restart generation
- [ ] #7 Telemetry bounded (event ring, request journal) with consumer-detectable sequence gaps
- [ ] #8 Each fix ships with a fake-backed test; suite green, zero analyzer warnings, zero-warning AOT publish
<!-- AC:END -->
- [x] #9 The catalogue probe path is configurable (today it asks `/models` while inference goes to
  `/v1/models`); shipped configs and the fake engine stop relying on that asymmetry
- [ ] #10 When every engine is unhealthy the OpenAI surface returns a prompt no-capacity error
  instead of queueing forever, with the deadline visible to the client
- [x] #11 The test fake honours `LoadedModels` so availability logic is actually under test

Progress: #9 and #11 are done (the probe path was already configurable via `models_endpoint`; it is
now pinned by `samples/router.toml`, `samples/router.ci-smoke.toml` and a loader test; the fake engine
now reports a mutable loaded set, which made the auto-switch test prove that a lifecycle command
changes what the engine serves). #10 remains open.

PLAN REVIEW in flight (deep tier) against 9e08e65; findings will be reconciled into this plan before implementation starts.

## Finding while documenting the agent surface (a3eb704)

Admission does **not** clamp a caller's claimed priority: `X-Inference-Priority` and
`POST /admin/threads` accept any class (RouterCore.cs:89,96 take `opts.Priority` verbatim), and the
admin surface has no authentication at all. So an untrusted local process can promote itself to
`interactive` and take the queue share a foreground agent was promised. This belongs with AC #5:

- Pin a ceiling at the HTTP edge (config `thread_priority.ceiling`, default = `default_priority`),
  clamping class **and** sub, and record `thread.priority_clamped` on the ring when it happens.
- Cover it in the error-contract table test: same header path, both the accepted and clamped value.
- Documented honestly in SPEC §6.1 and `docs/ai-integration.md` until then (loopback-or-proxy).

