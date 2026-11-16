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
