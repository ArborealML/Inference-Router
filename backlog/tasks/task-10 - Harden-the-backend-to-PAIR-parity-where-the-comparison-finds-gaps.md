---
id: TASK-10
title: Harden the backend to PAIR parity where the comparison finds gaps
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-17T03:39:00Z'
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
<!-- AC:BEGIN -->
- [x] #1 Client hang-up reaches the engine: the request is cancelled, its engine slot is released, and the ticket is counted cancelled rather than completed (pinned by wire tests)
- [x] #2 An engine error status or a failed stream always ends the response with the error frame and [DONE]; no channel is left open for the caller to wait on
- [x] #3 Ingress bodies are bounded (max_request_body_bytes) and the refusal is the router's own error contract, not the host's
- [x] #4 Engine URI composition is one rule across completions, health and catalogue probes, proven against an engine mounted under a base path
- [x] #5 Readiness is proven (health + loaded model + window fit) and its failure is one hard, actionable error with no silent fallback; switch is single-flight per engine with state reset on every failure path
- [x] #6 The error contract is table-pinned: codes, statuses and lowercase bodies pinned by contract tests
- [x] #7 Event-feed replay is correct: exclusive cursor, full retained window, atomic replay/subscribe handover, a slow subscriber's feed ended rather than pruned, subscribers bounded and over-limit feeds refused before SSE headers
- [x] #8 The feed records a terminal outcome before waking the waiter that outcome ends, so a failed request never outruns its own reason
- [x] #9 Every remaining parity gap has a named carrier (SPEC 14.1 rows 2/4/5/6, TASK-25, TASK-26, TASK-21) rather than being implied by silence
- [ ] #10 Request-side phase timeouts exist and are pinned: a bounded wait for a model switch, and a cap on an in-flight engine call so a wedged engine cannot hold a ticket until the caller gives up
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Event-feed correctness (exclusive replay cursor, full retained-window replay, atomic replay/subscribe handover, slow-subscriber feed closure, bounded subscribers) is being fixed in the ticket-surface task now, without changing the wire contract. What is deliberately left to this task is drop *accounting*: a slow subscriber's loss is currently visible only as a closed feed, and reporting it as an event kind is a wire extension that needs its own decision alongside the schema additions already listed here.

Cancellation had a hole that the AC checkbox hid. SPEC 5.5 says a client that hangs up takes the same path as DELETE /admin/tickets/{id}, but the HTTP layer never connected the request abort to the ticket: an abandoned stream was served to the end into a closed socket, kept its engine slot, and was counted as completed, and the pump swallowed cancellation and completed the ticket for the same reason. A stream whose engine answered an error status also left its channel open, so the caller waited forever for the error frame SPEC 6 promises. All three are fixed and pinned by fake-backed wire tests, which is what AC #8 is for: an AC that reads "cancellation reaches the engine" is only earned by a test that hangs up mid-stream. Remaining in this area, still open: the per-phase timeouts and the commit boundary (AC #2), and the fact that a disconnect is only noticed when the router next writes, so a caller that dies during a quiet stretch is noticed late.

Feed ordering is now an invariant, not a hope: every terminal outcome (engine switch, request completed/cancelled/failed) is appended to the ring before the waiter that outcome ends is woken — RunSwitchAsync and RouterCore.Complete/Fail (commit 0039b1e, SPEC 10 + revlog v0.12.3). That is what de-flaked the suite's last known flake and it is pinned by an assertion on event Seq order in the readiness-timeout test.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T03:25:00Z

---

Flaky under load, twice in a row at 18d7279 while the box was busy: EndToEndTests.A_Switch_That_Never_Becomes_Ready_Fails_Once_With_An_Actionable_Message fails the Assert.NotNull on the engine.switch_failed event in a full -m:1 run but passes 3/3 in isolation. It is time-based (WaitTimeoutMs = 1500 with a 20 s outer wait), and it is the AC #2 phase-timeout area: a test whose assertion depends on wall-clock luck will read as a regression every time the runner is loaded. Make the switch's readiness clock injectable, or assert on the event after awaiting the terminal state rather than racing it.

---

created: 2026-11-17T03:25:00Z

---

Correction to the previous comment: observed once, not twice — the next full run was green. Still worth de-flaking before it becomes a regression everyone re-runs.

---

created: 2026-11-17T03:39:00Z

---

The flake in comments 1-2 had a real cause, not a slow box: the switch outcome was appended to the feed *after* the shared readiness task was resolved, so the test awaited a terminal ticket state and then looked for engine.switch_failed, which had not been written yet. Fixed in the product rather than in the test (0039b1e): the feed records the outcome first, then wakes waiters. Evidence: the test passed in isolation before the change and failed in a full run on a loaded box; after the change, four concurrent full-suite runs are green (137/137 each), Debug and Release, and the AOT publish stays trim-clean. The test now also asserts switch_failed.Seq < request.failed.Seq, so the ordering cannot silently regress.

---
<!-- COMMENTS:END -->

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

0. **AC #1 — contracts and URI composition (LANDED).** One join rule (`EngineRuntime.Combine`) now covers
   completion calls, the health probe and the catalogue probe: leading '/' = absolute from the server root,
   bare name = appended to the endpoint's path, '?' supplies the query, trailing slash on `endpoint` absorbed.
   14 composition cases plus a wire test that asserts the paths the fake was asked for. Error bodies were also
   made lowercase on every surface (they contradicted §12 and the OpenAI claim) and `ErrorContractTests` pins
   code→status and casing. Live proof against strata: through the router an empty-messages POST now reaches
   `/v1/chat/completions` and comes back 502 `engine_error` with the engine's own 400 text; the old root path
   404s.
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
- [x] #1 URI/path composition tested (base endpoint + probe paths, trailing slash, query string, relative `health_probe.path` resolution)
- [ ] #2 Request bodies bounded; each phase (connect, headers, first token, idle stream) has a timeout; an explicit commit boundary decides whether in-flight work is recovered or declared lost
- [x] #3 Cancellation reaches the engine and releases reserved capacity; a queued ticket can neither spin nor starve (aging bound pinned by a test)
- [ ] #4 Switches serialized and coalesced; every failure path resets state; the error contract (codes + payload shape) pinned by a table test
- [ ] #5 Ingress auth/TLS/rate caps and engine API keys enforced; resource identity fail-closed (unlabelled/unowned containers refused)
- [ ] #6 Config reload atomic with the config pin recorded; engine crash produces readiness cleanup and a restart generation
- [ ] #7 Telemetry bounded (event ring, request journal) with consumer-detectable sequence gaps
- [ ] #8 Each fix ships with a fake-backed test; suite green, zero analyzer warnings, zero-warning AOT publish
<!-- AC:END -->

## Lifetime slice (AC #3) — what landed

- The caller's `CancellationToken` is wired to the ticket at enqueue, so a client disconnect takes the
  same path as an explicit cancel: a queued request gives its place back, an in-flight one cancels the
  upstream engine call and releases the engine slot.
- Exactly one terminal transition per ticket (complete / fail / cancel / queue rejection / queue
  deadline / shutdown all claim it atomically), so exactly one terminal event is emitted and reserved
  capacity is released once.
- `max_queue_time` is now a timer armed at enqueue; it bounds the wait, not the execution.
- Dispatch is level-triggered on engine state (`engine.health`, `engine.profile`, `engine.switch_*`).
- Shutdown fails still-queued tickets instead of leaving relays open; the ticket table is bounded to the
  4096 most recent finished tickets.

Two defects the new tests exposed (both real, both pre-existing): a request enqueued before an engine's
first health probe sat in the queue beside a free slot until something unrelated woke the dispatcher; and
`Task.Run(…, _shutdown.Token)` returned a never-running task during shutdown while the thread and engine
slots reserved at pop stayed held. Pinned in `TicketLifetimeTests` (5 tests).

Still open in AC #2: request-body cap, per-phase timeouts (connect / headers / first token / idle stream),
and the explicit commit boundary — the shipped post-commit framing (error frame then `[DONE]`) is what the
docs currently describe, so the boundary change updates them with it.
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

## Slice 1 facts (URI composition + error casing)

Verified against the live strata engine (read-only probes; no tokens generated) at 329970a:

| probe | result |
| --- | --- |
| `GET /health`, `GET /models`, `GET /v1/models` | 200 |
| `POST /chat/completions {}` | 404 `{"error":{"message":"not found"}}` |
| `POST /v1/chat/completions {}` | 400 "messages must be a non-empty list" |
| router `POST /v1/chat/completions` (same body) | **502 `engine_error` with that 400 text verbatim** + `X-Inference-Ticket: t00000005-4cef` |

So the shipped sample (`endpoint = "…/v1"`) was unreachable for completions before this fix: the base path was
dropped and every completion went to the server root. The probe paths kept working because they are written
root-absolute (`/models`, `/props`), which is what makes the single join rule worth having.

Two further findings for later steps:

1. **`auto_switch = false` + a catalog model the engine is not currently serving queues a ticket that never
   resolves.** With `default_model = "qwen-3.8-flash-next"` (the sample catalog name) against an engine loaded
   as `qwen3.8-flash-next-iq3_s`, the ticket sat in `WaitingForModel` for the whole 20 s of the client timeout
   — no deadline, no `model_unavailable`. Step 2 (ticket CTS + phase deadlines) is what makes this answerable;
   AC #2's deadline set must include the model-wait phase, not just connect/first-token/idle.
2. **The sample's catalog names do not match the ids strata reports on `/models`.** Either the sample gains the
   loaded id (or an alias) or the loader test should assert the catalog id appears in the engine's reported set
   for a documented live configuration. Belongs with TASK-14 (README/sample patterns), not here.
