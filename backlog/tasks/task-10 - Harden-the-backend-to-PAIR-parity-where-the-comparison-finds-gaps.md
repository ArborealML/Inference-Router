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
## Plan (ordered; each step ships with fake-backed tests per AC #8)

Facts this plan is written against: one flat `request_timeout_seconds` per engine and an infinite
`HttpClient` timeout so long streams survive (`EngineRuntime.cs:67`); switch single-flight with
per-target latched state (`RouterCore.ExecSwitchAsync`, `_switchState`); slots counted per engine with
lifecycle waiters excluded since 8af6aac; events through a bounded `EventRing`
(`Events/RouterEvent.cs:33`); config parsed once at startup with no reload path at all.

1. **AC #2 — bounds and phase deadlines.** Add `max_request_bytes` (ingress, enforced before any engine
   call) and an upstream response cap enforced by a counting relay stream that fails the request when the
   engine exceeds it. Split the flat timeout into phases: connect/response-headers, first-delta, and
   inter-delta idle (`first_delta_timeout_ms`, `idle_delta_timeout_ms`). Commit boundary: once the first
   delta reaches the client the response is committed, so a mid-stream failure is declared lost
   (event `engine.stream_aborted`), never replayed.
2. **AC #4 — switch contract.** Audit every failure path (engine gone, command exit≠0, command timeout,
   model never becomes ready) so per-target state resets and every waiter gets the same terminal answer;
   pin the wire error contract with a table test (status ↔ error code ↔ payload shape).
3. **AC #3 — cancellation, capacity, aging.** Cancellation must reach the upstream request and release
   the slot exactly once; a queued ticket must neither spin nor starve — pin an aging bound with a test
   that a low-priority ticket eventually runs behind a busy model.
4. **AC #7 — bounded telemetry.** Monotonic sequence numbers on event frames plus a drop counter, so a
   consumer detects a gap instead of silently missing history; keep the ring bounded and add a bounded
   recent-request journal surfaced through the admin surface.
5. **AC #5 — ingress protection.** Optional shared-key auth on `/v1/*` and `/admin/*` (admin requires it
   by default), TLS via config, per-identity rate caps. Tests assert the engine API key actually leaves
   on the wire (fixture inspects `Authorization`). Fail-closed resource identity belongs to TASK-1 and is
   cross-referenced, not duplicated here.
6. **AC #6 — crash and reload.** Engine crash already clears readiness; add a restart generation (engine
   epoch) in `/admin/status` and events so a restart is visible. Config reload does not exist today:
   either land an atomic snapshot swap or record "no reload before v0.1" as an accepted gap in SPEC §14 —
   owner's call.
7. **AC #10 (done)** — a switch request with no capacity terminates on its own deadline as `engine_error`
   rather than `queue_timeout`; regression test in 8af6aac.

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
