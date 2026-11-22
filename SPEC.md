# Arboreal Inference Router — Specification

Status: draft, continuously revised (v0.12.141)
Target: .NET 11 (10 acceptable; 11 SDK 11.0.100 installed), Native AOT everywhere.
Repository: `ArborealML/Inference-Router` on GitHub (public); local checkout
`~/src/arboreal/inference-router`. Working notes live in `.local/`, which is gitignored.

## 0. Revision log

- v0.12.141 — the CLI prints native Bash, PowerShell, Zsh and fish completion registrations. One read-only candidate service supplies positional options and configured engines, models, aliases and profiles (§11).
- v0.12.140 — cancel a local owner waiting for the target switch claim. The group holds an issued transaction until it unwinds (§7).
- v0.12.139 — bound local Docker group preparation by the original request lifetime and verify peer inventories at mutation boundaries (§7).
- v0.12.138 — the CLI reports refused event subscriptions with exit 1, labels unreported serving capacity, and includes probe and selector metadata in JSON config readback (§11).
- v0.12.137 — allow five minutes per Docker API call by default. Later local-group requests reconcile outstanding Docker operations against fresh group inventory. Unresolved occupancy fails the request (§5.3.1, §7).
- v0.12.136 — render CLI operator queries and acknowledgments as human-readable text by default. A global `--json` flag selects machine output (§11).
- v0.12.135 — check exact profile readiness after verified offer validation and before issuer checkout. A changed observation returns `switch_failed` (§6).
- v0.12.134 — distinguish equal-window serving tiers by measured slots, drain exact profile users before explicit switches, and verify counted transports at submission (§6, §7.6).
- v0.12.133 — expose the applied catalog with conditional reads and changes on the event feed. Inference clients can select an exact named serving profile per request (§6, §7.6).
- v0.12.132 — local configuration commands and the standalone server default to `~/arboreal/inference-router/router.toml` (§11).
- v0.12.131 — local requests share available slots on a fitting resident profile. A local switch wait expiry or lifecycle refusal returns the existing switch failure response, with the refusal reason on the event feed. A local deadline reports the wait ceiling even when preparation has yet to start.
- v0.12.130 — a local group may replace one confirmed owned resident on launch-setting drift or failed backend readiness. It rechecks group occupancy and the same container generation before stopping. The original queued request owns the replacement and keeps its switch deadline. Failed replacement is terminal for that request.
- v0.12.129 — on first admission a local Docker group can reuse its verified running target. The group checks its inventory, effective Docker configuration, backend readiness and container start identity before dispatch. Ambiguous occupancy or contradictory adoption evidence refuses the request before mutation.
- v0.12.128 — authenticated idle reload accepts additional native engines in existing local groups and additional aliases on retained group members (§7.7).

- v0.12.127 — dotted readiness probe paths can traverse zero-based JSON array indices, allowing model-list context gauges without a separate backend endpoint (§7.6). Invalid indices and missing values remain failed observations.

- v0.12.126 — local Python backend startup creates configured log, temporary and slot directories automatically. Native readiness retries a refused listener connection within the original readiness deadline rather than failing a container that is still loading. Between unsuccessful probe rounds it rechecks the exact started container and fails on exit or replacement.

- v0.12.125 — local lifecycle groups bind ordinary queue admission to shared-resource drain and native Docker switching on an existing daemon. Original request ownership, detached transport cleanup and independent group progress are exercised with three shared engines and four clients. Live acceptance remains pending (§7).

- Pending — initial topology validation accepts any nonempty configured engine and device set, requires exact complete membership and distinct routes, and projects every approved target. The two-engine example is not a cardinality limit.

- v0.1 — initial architecture + scope.
- v0.2 — added TOML config schema, engine lifecycle contract, thread model, AOT strategy, research questions.
- v0.3 — tightened scheduling semantics (prefix-aware scoring, prefill impact), added suffix-cache forward design, observability events, acceptance criteria, repo layout; recorded the research questions and their disposition.
- v0.4 — ASP.NET-under-Native-AOT research folded into §9 (RDG, CreateSlimBuilder, STJ mode guidance, SSE via TypedResults.ServerSentEvents, HttpClient type-info overloads, trim-warning discipline); added §13 Pi integration (mid-session model switching, intelligent wait protocol, design pass dispatched as a deep research question).
- v0.5 — implementation landed: core library (threads/priority, per-model priority queues, prefix trie + suffix extents, prefix-aware scheduler, engine runtime with health/lifecycle, TOML+JSON config), Server (passthrough minimal APIs, SSE keepalive while queued/switching, admin surface), CLI (serve/chat/complete/models/status/events/threads/switch/tickets), 41 passing tests incl. end-to-end dispatch against stubbed engines.
- v0.6 — Native AOT achieved end-to-end: TOML config via Tomlyn's Roslyn source generator over typed DTOs (ConfigTomlContext; no reflection/DOM), JSON twin via STJ source-generated context over the same DTOs, `IsAotCompatible` clean, `PublishAot` smoke produces a working stripped native binary with zero analyzer warnings (RDG intercepts Map* lambdas at publish, so compile-time IL2026/IL3050 on Map* are expected noise under IsAotCompatible builds only). Dispatch correctness hardening: engine capacity is reserved at dispatch time (drain loop cannot over-dispatch); capacity release re-wakes dispatch after EndEngineOnce; ModelQueue head-first ordering fixed (priority desc, FIFO asc, index 0 = next dispatch); scheduler compares canonical Ticket.Model, not the raw requested alias; engine health loop bootstraps an immediate probe at startup; wait_health=false switch refreshes loaded models before resolving ready; streaming chunks are copied out of the read buffer before channel write. samples/router.safe-smoke.toml added: inert (remote) engines for smoke tests so auto-switch never spawns real lifecycle commands.
- v0.7 — research dispositions folded in: Cysharp-style performance conventions (§9.1), TOML binder comparison (confirms the Tomlyn source-gen choice; CsToml is prozolic, not Cysharp), scheduling prior art (§12.2/§12.3/§12.4 with the score shape and the causal-suffix caveat), Pi extension design (§13.1 requirements P1–P10, §13.2 two-phase wait, §13.3 AIR surface delta), analyzer stack integrated (§9.2: central packages, banned-API policy, warnings-as-errors, 0-warning build + 0-warning AOT publish), Tomlyn DOM confirmation. New feature per owner: **serving profiles + context-window escalation** (§7.6) — per-model context tiers with slot counts, requests that outgrow the active window queue behind a profile switch rather than compaction; `health_probe.context_path`/`slots_path` gauges drive admission (`max_slots`) and escalation.
- v0.8 — **serving profiles + context-window escalation implemented** (§7.6): ladder in TOML/JSON (`serving`, `default_profile`), probe gauges (`n_ctx`/`total_slots`) refreshed by the health loop and by switch readiness, `EngineStats.active_profile/active_context_window/active_slots`, `engine.profile` events, scheduler escalation (smallest fitting tier, never blind when the window is unreported, engine skipped when its ladder cannot fit), admission = `min(max_inflight_per_engine, reported slots)`, `X-Inference-Required-Context` header, declared-context estimate via `scheduler.chars_per_token_estimate`, `/admin/switch {profile}`. Lifecycle exec hardened: argv re-quoted for the shell (spaces in one arg), stdout/stderr drained concurrently (chatty select-model no longer deadlocks a full pipe), timeout kills the process tree, and every failure path resets switch state (a failed switch could previously wedge an engine as "switching" forever). Bug found while testing: unquoted dotted model ids in the shipped samples silently nested into bogus catalog entries — samples fixed, quoting documented (§7), and a test now asserts the samples load as written. New `arboreal config` dry-run verb (§11). 64 tests green, build clean under warnings-as-errors, AOT publish trim-clean.
- v0.8.1 — public-repo hygiene: handoff/session notes moved to gitignored `.local/`, personal absolute paths and key-file locations replaced with neutral `/etc/arboreal/keys/...` in the §7 sketch and `samples/router.toml`, README.md added as the entry point. No secret material was ever committed (checked across full history).
- v0.8.2 — public hygiene: internal research-service names and request identifiers are gone from the spec (§15 now lists questions and dispositions by question only), personal machine paths use neutral placeholders, MIT licence added, AGENTS.md documents build/test/AOT conventions and safe smoke testing.
- v0.8.6 — native-lifecycle design review ingested (§5.3.1): lifecycle is one host transaction with
  proven ownership/assets/image pins, admission close, cohort drain, exact-target launch, layered
  readiness proof, no automatic retry/rollback/readiness fallback, single-flight per target with latched
  failures, advisory journal and no adoption of unowned resources; ids not names. Seam decided:
  `IEngineLifecycleProvider` + coordinator, first Docker provider is raw Engine HTTP over the socket
  (the CLI-first preference of the design review is recorded and declined, with the CLI kept as an
  operator fallback); `select.exec` survives as a mutually-exclusive delegated provider. Also recorded:
  a switch must be a *lifecycle* reservation, not inference capacity, once the router drains cohorts
  itself — today's code holds an engine slot across the switch, which is fine for a single-model engine
  and would misreport busyness later.
- v0.8.5 — two external reviews ingested. **Docker access under AOT**: no client library; raw Docker Engine HTTP over the socket with source-generated JSON, pinned API version, exec frame demux, and strict error fidelity (404 = absent, transport failure = daemon unavailable) — disposition in §15, seam in §5.3. **Robustness comparison** against a mature local-inference router: we are not yet as robust single-host, so §14.1 records the parity bar and its ordering, §10 records the event/admin-feed prerequisites a live view needs, and §12.5 records which UI ideas to borrow, change or skip. Sample ladder probe results in §0/v0.8.4.
- v0.8.4 — performance claims put behind a harness: `bench/Arboreal.InferenceRouter.Benchmarks`
  measures engine-direct / router-enqueue / router-http at concurrency 1, 4 and 16, non-stream and
  65-frame stream, with one shared in-process fake engine so each delta is router work. Results and
  caveats in `docs/benchmarks.md`; §9.1 now quotes them instead of promising them. The harness
  caught a real defect: the SSE relay polled every 200 ms for the ticket's stream channel, so
  time-to-first-token could stall a whole interval. The channel is now created at admission (before
  `Executing` is published) and `RouterTicket.StreamReady` resolves it for the HTTP relay, which
  emits keepalives every 4 s instead of polling; two regression tests added. 66 tests green. Sample
  serving ladder probed against a live engine (`n_ctx` 524288 / `total_slots` 1 → measured `512k-1`
  tier added to `samples/router.toml`); the remaining tiers are annotated UNVERIFIED with the probe
  each of them needs.
- v0.8.3 — history rewritten (force-pushed) so the published history contains neither the machine-specific paths above nor the unpublished working notes: the two commits that only added working notes were dropped, and every remaining commit's spec/sample blobs were scrubbed with the same rules as v0.8.1–v0.8.2. Keep it that way: nothing internal, personal or service-specific goes in tracked files.

- v0.8.7 — CI and tagged releases wired: `ci.yml` (build + tests + trim-clean AOT publish on every
  `main` push and PR), `release.yml` (tag preflight that refuses an existing tag/release **before
  any build**, four stripped Native-AOT assets built on native runners, per-platform smoke test as
  a hard gate, draft release published only after all assets upload, draft cleanup on failure),
  `tools/ci/smoke.sh` + `tools/ci/fake-upstream.py` + `samples/router.ci-smoke.toml` as the smoke
  harness, and the CLI executable renamed to `air`. Two CLI defects found while building the smoke
  harness are fixed: POST commands handed `HttpClient` a relative URI (every `chat`/`threads`/
  `switch` call failed before reaching the router) and the config environment variable was
  misspelled (`AIRBODAL_ROUTER_CONFIG` → `ARBOREAL_ROUTER_CONFIG`).
- v0.8.8 — release pipeline exercised on Actions: a dry run built stripped Native-AOT `air` for
- v0.8.9 — agent integration surface documented and pinned to the code: `docs/ai-integration.md` (thread identity, context-demand header, keepalive/abort framing, admin surface, error-code table, worked context-overflow session) and `samples/pi/inference-router-tools.json` (descriptive tool manifest; nothing reads it). New: every admitted request returns `X-Inference-Ticket`, so a client can `GET|DELETE /admin/tickets/{id}` without scraping the event stream. §6 text corrected to the shipped framing (`data: [router keepalive seq=N status="…"]` every 4 s, status only on change; a failed stream ends `data: {"error":…}` then `data: [DONE]`). §6.1 now states the ingress posture honestly: priority is **not** clamped to the configured default and the admin surface is unauthenticated, so deployment is loopback-or-behind-a-proxy until the v0.1 ingress work. A new wire test replays every path/method in the manifest against a live `ServerApp`, so a renamed route or a phantom tool fails the build. 69 tests green.
- v0.9 — engine URI composition made coherent (§7.5): one join rule for completion calls, the health probe and the catalogue probe (`EngineRuntime.Combine`) — a leading '/' is absolute from the server root, a bare name appends to the endpoint's path, '?' supplies the query, and a trailing slash on `endpoint` is absorbed. Completions now append, so an engine mounted at `…/v1` is called at `/v1/chat/completions`; previously the base path was dropped and such engines answered 404. Proven against the live strata engine: through the router an empty-messages request returns 502 `engine_error` carrying the engine's own 400 text verbatim (it reached `/v1/chat/completions`), where the root path 404s. Error bodies are now lowercase on every surface (`{"error":{"type":"air_error","code":…,"message":…}}`) — they were PascalCase, which contradicted §12 and the OpenAI-compatibility claim — with the type value aligned to `air_error`; SPEC §6.1 carries the shipped code→status table and `ErrorContractTests` pins the mapping and the casing. New: `UriCompositionTests` (14 composition cases + a live-wire assertion on the paths the fake engine was asked for), and the test fake can answer completions with a chosen status. 87 tests green.
  `linux-x64` and `win-x64` and passed `tools/ci/smoke.sh` on each of its own runners, and the preflight
  refusal is proven (a tag with a leftover draft release failed before any build job started). Windows
  needed `-p:` property switches rather than `/p:` (Git-Bash rewrites `/p:x=y` into a path; the leg failed
  with MSB1008 first), and preflight needs write scope because the releases-by-tag API hides drafts. The
  macOS legs never got a runner, so osx artifacts are designed but unproven (TASK-15). Availability got
  real: the test fake now serves a mutable loaded set (`LoadedModels` / `LoadedModelsFile`), so the
  auto-switch test proves a lifecycle command changes what an engine serves, and the catalogue probe path
  (`models_endpoint`; `/models` by default, `/v1/models` for OpenAI-only engines) is pinned by samples and
  a loader test.
- v0.10 — ticket lifetime (§5.5). The caller's `CancellationToken` is now wired to the ticket at
  enqueue: a client that hangs up takes the same path as an explicit ticket cancel, so a queued request
  loses its place and an in-flight one cancels the upstream engine call and releases the engine slot
  (`cancelled`). A ticket now has exactly **one** terminal transition — complete, fail, cancel, queue
  rejection and queue-deadline all claim it atomically, later racers are ignored — so exactly one of
  `request.completed` / `request.failed` / `request.cancelled` is emitted and reserved capacity is
  released once. `max_queue_time` is enforced by a timer armed at enqueue rather than a dispatch-time
  check. Dispatch is level-triggered on engine state as well as edge-triggered by arrival. Shutdown
  gives still-queued tickets a terminal state instead of leaving relays open. The ticket table is bounded
  to the 4096 most recent finished tickets. Two defects the new tests caught: a request enqueued before
  an engine's first health probe sat in the queue beside a free slot until something unrelated woke the
  dispatcher, and `Task.Run(…, _shutdown.Token)` handed back a never-running task on shutdown while the
  thread and engine slots reserved at pop stayed held. 92 tests green; test classes no longer run in
  parallel (in-process routers and servers starve each other's dispatch threads, which showed up as
  lifetime timeouts unrelated to the code under test).
- v0.11 — an agent can now follow **its own** request. `GET /admin/tickets/{id}/events?since=<seq>`
  replays one ticket's history and live tail on the SSE feed; `/admin/events` gained the filters
  `kinds=`, `thread=`, `engine=`, `ticket=` (§6). The filter is applied to replayed events as well as
  the live tail — previously `SubscribeEvents` replayed the whole ring unfiltered, and its live path
  used a single-slot completion source that dropped events arriving while the consumer was busy.
  Subscribers now have a bounded buffer (256 events, drops preferred over growth) and the monotonic
  `Seq` makes any drop visible to the consumer (§10). Headers flush before the first frame, so a
  stream that matches nothing is a quiet 200 rather than an apparent hang. `X-Inference-Ticket` was
  already on every admitted response. Casing of the admin feed stays PascalCase and is now pinned by
  a test; unifying it with the lowerCamel names that §10 and the tool manifest use is §15.1 #1.
  95 tests green; wire tests share one `WireHarness` instead of three copies that can drift.
- v0.11.1 — the replay and reconnect contract pinned to the code. `since=` is exclusive, matching the
  filter that reads it. Replay returns the whole retained window: a default cap of 1024 was cutting a
  4096-event ring down to a quarter of what a reconnecting client was entitled to, with no signal. A
  cursor ahead of the ring computed a negative count and threw **after** the SSE headers were flushed,
  so the client saw a truncated 200 instead of an error; it now yields nothing. Replay snapshot and
  live subscription happen under one ring lock — an event appended in the old handover window reached
  neither path. A subscriber that falls behind has its feed ended instead of its writes dropped
  (`DropWrite` was uncounted, and a filtered reader has no `Seq` gap to notice); §10's "any drop is
  visible" claim was true only of an unfiltered feed and is corrected here, as is the same claim in
  `docs/ai-integration.md`. Caller cancellation now stops delivery instead of draining the buffer to a
  hung-up reader, the router's own shutdown ends every feed, and live subscribers are bounded by the new
  `max_event_subscribers` key (256 by default, counting the router's own internal subscription), with a
  feed that arrives past the limit refused `503 too_many_subscribers` before any SSE headers are sent —
  a 200 that ends at once would read as a filter that matches nothing. An external review of that
  commit then found two more cursor holes, both fixed here: `since=<long.MaxValue>` overflowed the
  checked arithmetic in the replay helper and threw after the SSE headers were flushed, and a cursor
  ahead of the ring — a client reconnecting after a restart — yielded nothing from replay **and**
  filtered out every future event, so the feed was alive but permanently silent. The cursor is now
  clamped to the ring head, and `Replay` refuses the arithmetic rather than the overflow. What remains
  honestly unrepaired is stated rather than glossed: an ended feed says "reconnect", not "N events were
  lost", it is indistinguishable from a feed that ended normally, and nothing separates a restart from a
  quiet period because the ring carries no epoch. Those need the wire extension, not another local fix. Drop
  *accounting* and `stream.gap` remain open: they add an event kind, so they need a wire decision and
  belong with the parity work, not with this fix. 102 tests green.
- v0.11.2 — a streaming request that does not finish now ends the way §5.5 and §6 already said it
  would. §5.5 promised that a client which hangs up takes the same path as `DELETE /admin/tickets/{id}`;
  the HTTP layer never wired `RequestAborted` to the ticket, so an abandoned stream was served to the
  end into a closed socket, held its engine slot, and was recorded as `request.completed` — a truncated
  request indistinguishable from a served one, in the stats and in the event feed. The pump also
  swallowed cancellation and returned normally, which completed the ticket for the same reason. And a
  stream whose engine answered an error status left its channel open: the response was already a
  committed 200 event stream, so the caller sat waiting for the error frame §6 promises. Failure now
  completes the stream channel, cancellation propagates to a `cancelled` terminal state, and the
  request's own abort cancels the ticket. New `StreamingFailureTests` pins both cases against a live
  `ServerApp`, and the test fake can trickle a stream frame-by-frame so a mid-stream hang-up is
  observable rather than assumed. 109 tests green.
- v0.12 — model loading runs through a **provider seam**, and the router can now drive Docker itself.
  §5.3.1 was design prose; the seam is now what the switch path actually runs. `IEngineLifecycleProvider`
  plus `EngineLifecycleCoordinator` own one ordering — prove ownership, preflight, stop only what the
  ownership proof claims, launch the exact target — and the selector program that `select = { exec = … }`
  always meant is wrapped as `DelegatedExecProvider`, same argv quoting, same pipe draining, same timeout
  kill, so every existing config switches exactly as before with one more hop. `DockerLifecycleProvider`
  speaks raw Engine HTTP (unix socket or TCP), pinned API version refused downward, **source-generated**
  JSON, containers labelled `arboreal.router`/`arboreal.engine`/`arboreal.model`, image presence required
  and no pull ever, and a daemon that is unreachable, refuses the router, or accepts a call and then goes
  quiet (each call carries its own deadline) yields one actionable message with no attempt to escalate. Three rules that used to be implied are now pinned by tests: a refused
  preflight stops and starts nothing; a container the daemon lists but our labels do not claim is never
  handed to the stop step; and a **failed ownership query is a refusal, not an empty list** — treating
  "I could not ask" as "nothing is running" is how a router starts a second copy of a model that is
  already loaded. Config is per engine (`lifecycle = "docker"` + `[engine.<name>.docker]`), and a model
  that carries both a `select` command and native lifecycle is a load-time error, because two owners
  cannot both prove ownership. What §5.3.1 still does not claim: image identity pinning, mounts/GPU
  requests, exec/logs/daemon events, Windows `npipe`, closing admission and draining the cohort before a
  mutation (it needs a lifecycle reservation, not an inference slot — that request would wait on itself),
  and latched failures. 126 tests green.
- v0.12.2 — the Docker provider is hardened against the ways a shared daemon bites back (external review,
Docker lifecycle). A published port now always names the interface it binds — `host_ip`, default
`127.0.0.1`, because an empty `HostIp` is Docker's shorthand for every interface and the router has no
ingress credential. `router_id` is **required** rather than defaulted to `"default"`: the label is the
ownership proof, and two routers sharing one daemon with the same id could each stop the other's
containers. A switch no longer leaves its stopped containers behind — `stop` now removes them
(`remove_stopped`, default true) — and `stop` accepts `304`/`404`/`409` as "already in the state we
want", so the second switch on an engine is not refused by the residue of the first. `start` accepts only
`304`: a `404` there means the container the daemon just created is gone, and reporting a successful
launch with nothing running is worse than failing. A `200` whose body cannot be parsed is a **failed
ownership query**, not an empty list (the old `?? []` turned a weird daemon answer into a second copy of a
loaded model), the list filter is built through the source-generated context instead of pasted JSON, since
`router_id` and the engine name are config text, and container ids are URL-escaped on the way into paths.
The HTTP handler sets `UseProxy = false` and `AllowAutoRedirect = false` — a system proxy or a daemon's
redirect would send container control to a third host — and a bare `socket` value is read as a socket path
rather than a host name. Single-flight is **per engine**, not per target: two switch requests for
different models on one engine serialise, which is stricter than the target's identity but matches one
daemon transaction per engine. 137 tests green.
- v0.12.1 — a request body has a stated limit. The router buffers a body to forward it as bytes, so an
  unread limit was also no bound on what one request could make the process hold; `max_request_body_bytes`
  (16 MiB by default, refused below 1 KiB) is checked against `Content-Length` as a fast path and against
  the counted copy, so a caller that understates its length is still cut off. Refusal is the router's own
  error contract — 413 `request_body_too_large`, lowercase like every other error body — rather than
  whatever the host would have answered. 130 tests green.
- v0.12.3 — the event feed records a terminal outcome **before** it wakes the waiters that outcome ends.
  `EngineRuntime.RunSwitchAsync` completed the shared readiness task and only then appended
  `engine.switch_failed`/`engine.switch_completed`, and `RouterCore.Complete`/`Fail` resolved the ticket's
  completion task before appending `request.completed`/`request.failed`/`request.cancelled`. Both left a
  window: a ticket could be observably `Failed` (caller already answered 503 `switch_failed`) while the
  feed still had nothing to explain it, and a client that awaited its ticket and immediately replayed its
  own history (`/admin/tickets/{id}/events`) could read a stream that had not reached its ending. An
  operator watching `/admin/events` saw a failed request with no reason and no way to tell whether the
  router was retrying. Every path now appends first and wakes waiters second. This is the §10 ordering
  rule above, and it is what de-flaked the readiness-timeout end-to-end test, which had been racing
  exactly this window (it failed under a loaded runner and passed in isolation; three concurrent
  full-suite runs are green after the fix, and the test now asserts the event ordering itself).
  137 tests green. §14.1 also gains a per-row status of the robustness bar: what is closed, what is an
  accepted gap with a named carrier (request-side phase timeouts; lifecycle reservations and the §5.3.1
  non-claims via TASK-25/TASK-26; loss reporting behind the live-view decision in TASK-21), and what is
  deliberately not started.
- v0.12.4 — request-side phase timeouts, which closes the accepted gap in §14.1 row 2. Two new settings,
  one per wait that the queue deadline never covered. Per-engine `response_timeout_ms` (default 600000 ms,
  `0` disables) bounds how long an engine may sit on a request it accepted before it starts answering — the
  clock stops at the response's first bytes. A non-streaming completion is covered whole, because an engine
  sends its headers with the body; a stream is covered up to its first frame and never cut off after that,
  so a generation in progress is never truncated. An engine that accepts a request and then goes quiet fails
  the new `engine_timeout` (504) and hands its inference slot back, so one wedged backend costs one
  request rather than a slot. Scheduler `switch_wait_timeout_ms` (default `0` = no ceiling) bounds the
  wait a queued request pays for a model switch it triggered: over the ceiling the request fails
  `switch_failed` and its slot is released, while the switch runs on under the select command's own
  `wait_timeout_ms` — the ceiling protects the caller, and the engine's switch state machine stays
  authoritative about what is loaded. The response timer deliberately rides the request's own
  cancellation token, so a caller that hangs up still ends `cancelled` and only a silent engine ends
  `engine_timeout`; `RequestPhaseTimeoutTests` pins that distinction along with the 504, the slot being
  the only thing freed, and a switch ceiling that ends the request in well under the select command's own
  timeout. `EngineRuntime.ResponseTimeout` is resolved once from config rather than re-derived per
  request, and `RouterCore.ExecuteAsync` now shares one `FinishWithResponseAsync` path between the capped
  and uncapped cases so the two cannot drift apart. 141 tests green; AOT publish stays warning-free on
  linux-x64.
- v0.12.5 — the deep review of v0.12.4, dispositioned; and three sentences in the v0.12.4 entry below
  described an intention the code did not keep, so read this one instead. The response ceiling now bounds
  what the caller is still **owed** rather than the arrival of headers: a non-streaming completion and an
  upstream error body stay under the gate until the body is read, so an engine that sends headers and then
  goes quiet ends `engine_timeout` instead of hanging until the client gives up, and a stream that never
  produced a frame is bounded by its first-frame ceiling too. `RouterCore` gained a `ResponseGate` with its
  **own** cancellation source — the v0.12.4 claim that "the response timer rides the request's own
  cancellation token" was false — and the arbitration now asks who won before it labels anything: a caller
  that hung up reports `cancelled` even against an upstream handler that ignores cancellation, so an abort
  can never be reported as `engine_timeout`. A response that arrives after the ceiling wins is disposed
  rather than leaked. On the switch side the change is ownership: a switch runs on a token the **engine**
  owns rather than the initiating request's, so a waiter that gives up neither stops the switch nor latches
  a failure for everyone else; a waiter with a different target takes its own turn instead of being failed
  on someone else's outcome, and a switch that fails instantly says what failed rather than "within N ms".
  A ticket's terminal transition owns closing its stream channel rather than the read pump's `finally`,
  which fixes a defect found while testing this and not reported by the review: a stream whose engine sent
  headers and no frame simply stopped, with no error frame, because the relay saw a non-terminal ticket.
  Streaming ceilings are in-band by construction — the `200` is already committed — and §6 now says so: a
  504 means nothing was emitted yet, so a 504 stays safe to retry. Two review findings are accepted gaps
  with their reasons in §15 (the selector's reason string stays on the feed; an over-large timeout value
  is caught at DTO binding, not by the router's own message). New end-to-end fake modes
  (`HeadersThenStallCompletions`, `AnswerAfterCancellationMs`, `StallingStream`) and five new
  `RequestPhaseTimeoutTests` pin the behaviour; 146 tests green in Debug and Release, AOT publish stays
  warning-free on linux-x64.
- v0.12.6 — the second deep review (of v0.12.5) dispositioned. One finding is a live correctness bug worth
  naming: a request waiting for a **serving profile** could be answered Ready by a switch to a *different*
  profile of the same model, because the switch machine compared only the model id — the request would then
  be dispatched against a context window the engine does not serve. A target is now model **and** profile
  wherever the switch machine decides, pinned by `SwitchFairnessTests`, which fails against v0.12.5 in both
  directions (a stranger's success must not answer you; a stranger's failure must not condemn you). The
  shutdown race in the same finding is fixed: the switch token was read inside the scheduled task, so a
  `DisposeAsync` landing in that window raised `ObjectDisposedException` before `RunSwitchAsync` was
  entered, with no `finally` left to settle the readiness task — every waiter on that engine then sat out
  its ceiling on a switch that never ran. The token is read before scheduling, its source is deliberately
  not disposed (an in-flight switch still holds it), and a switch that cannot start settles its waiters and
  puts the engine back. Two ordering rules follow: every switch exit clears the engine's "switching" state
  *before* waking waiters, so a waiter that re-checks starts its own switch rather than rejoining one that
  is already resolved; and the response gate is passed the moment the owed bytes are in hand — before any
  terminal work — so a failure inside the router's own completion path can never be relabelled
  `engine_timeout`. Stream acquisition sits inside the gate too, and switch-failure details now name the
  target that failed rather than whichever switch happened to be in flight. Two findings are accepted with
  their reasons in §15: a ceiling that loses the race to bytes already in hand does not fire, and there is
  no idle bound after a stream's first frame — that gap, and the unbounded buffering of a reader that stops
  consuming, are TASK-30.
- v0.12.7 — the published release surface is two platforms, not four. `release.yml` drops the
  `osx-arm64` (macos-14) and `osx-x64` (macos-13) matrix legs, and its publish gate now demands both
  declared archives rather than four; the README install and release copy names the same two. The reason
  is evidence, not portability: the macOS jobs queued and were never picked up, so the gate could never
  be satisfied while the public repository advertised binaries it had never produced. Nothing in the
  router is Linux/Windows-specific, so this is a publishing decision and not a port — the Cli project
  keeps all four RIDs and `dotnet publish -r osx-arm64` stays a build-it-yourself path, but no macOS
  artifact is built, published, claimed or gated. §14 states the published surface; no wire behaviour
  changes.
- v0.12.8 — the release gate hardened after a deep review of v0.12.7. It now refuses an empty archive
  or an empty sidecar rather than only a missing one; it verifies every archive against the checksum its
  own build leg recorded before publishing a set, instead of hashing whatever was downloaded and calling
  that a checksum; and it refuses an archive count that differs from the declared leg count, so a matrix
  leg added without editing the gate cannot be published ungated (the publish step globs, which is why
  the count is checked). A tag with a `-suffix` now publishes as a GitHub prerelease: preflight already
  admitted such tags, and they would otherwise have been released as final versions. README and §14 say
  plainly that the two-platform surface is configured and not yet proven — no tag has been published, so
  the gate has never run end to end.
- v0.12.9 — the release gate now verifies bytes it can tie to the leg that built them. The deep review of
  v0.12.8 caught that `sha256sum --check sidecar` follows the filenames written *inside* the sidecar: two
  sidecars naming the Linux archive plus an unrelated Windows archive passed the gate, and the unchecked
  Windows bytes would have shipped. Each archive is now compared against the whole record its own leg
  produced — digest and filename together — so an archive whose bytes move afterwards fails. Downloaded
  artifacts are no longer flattened into one directory: each leg artifact keeps its own folder, an artifact
  named for no declared leg is refused, a file inside a leg folder that leg does not produce is refused,
  and a basename arriving twice is refused instead of silently overwriting. Each archive must also unpack
  and contain its `air` / `air.exe` entry. The upload list is now built from the same `EXPECTED_RIDS` list
  the gate reads, replacing the publish globs, so nothing can be published that was not gated. The release
  is created as a draft, read back from GitHub while it is still unpublished (asset inventory, `draft`,
  `prerelease`), and only then made public, with the published state asserted afterwards; a failed run
  deletes by release id and only when that draft was created at or after the run started, so a race with
  another run cannot destroy its release. The checksums remain a damage check and not provenance: nothing
  here signs the artifacts or proves which runner wrote them. No tag has been published, so the gate is
  still configured rather than proven.
- v0.12.10 — a second deep review of the gate (of v0.12.9) found four holes, each reproduced as a
  forged artifact before being fixed, and each now a standing case in the local gate harness. (a) The
  cleanup step decided ownership from the release's `created_at` against `github.run_started_at`, which
  is not a documented Actions context property: read as empty, the comparison protects nothing, and a
  draft someone else made for the same tag while this run built would be deleted. The create call's own
  release id is now recorded and is the only thing cleanup will delete — and only while it is still an
  unpublished draft of that tag; with no id captured, the step touches nothing. (b) The leg allowlist
  tested `EXPECTED_RIDS` for a substring, so one artifact named for two legs at once was admitted; the
  match is now word-for-word. (c) The archive member check used `grep -qx "$exe"`, a regex, so an
  archive whose only member was `airXexe` satisfied "contains `air.exe`", and an archive could carry a
  second member; members are now matched literally and the archive must hold exactly one. (d) Directories
  and symlinks were invisible to the inventory walk (`find -type f`) and a directory passes `test -s`, so
  the leg folder is now walked for every entry, counted against the two the leg produces, and each name has
  to be a regular non-symlink file. Alongside: the tag is re-resolved to a commit and compared against what
  the build legs checked out before anything is published (`--verify-tag` only proves a tag exists); every
  published asset must be in `uploaded` state with the staged file's size; the draft is addressed by its id
  rather than found by listing releases (which was one page of a hundred); preflight's existing-release
  check paginates and reports more than one release on a tag as a conflict; and an unknown RID has no
  implied binary name. Accepted gap, stated rather than fixed: the gate checks the remote asset's name,
  state and size but does not download the served bytes and hash them, so corruption inside GitHub's own
  storage between upload and publish is not detected — the published sidecars and `SHA256SUMS` cover the
  downloader, not the gate. The first tag run still has to confirm the real `download-artifact` layout, the
  `gh release create --json id` output, draft visibility, and asset `state`/`size` against live uploads.
- v0.12.11 — the published half of the gate could never have published anything, and a third deep
  review of the gate said so before any tag ran. `gh release create` has no `--json`/`--jq`: against
  gh 2.46.0 the shipped call fails at argument parsing (`unknown flag: --json`, exit 1) before it
  reaches the API. `gh release view`/`edit` also resolve their operand as a tag name, not a release id
  (`gh release view 443387629 -R cli/cli` answers `release not found`), so the id-addressed publish
  that v0.12.10 described was not id-addressed at all. Create now passes only flags the CLI really
  has, and every remote call after it is REST: the id comes from `GET /releases/tags/{tag}` — sound
  because a create for a tag that already carries a release is refused, so after a successful create
  the release carrying the tag is the one this run made — publish is `PATCH /releases/{id}` and
  cleanup is `DELETE /releases/{id}`, and the published record is read back by id. The step records a
  `create_attempted` output before calling create, so a run that dies between the call and the id
  capture leaves a loud "manual review required" annotation rather than a quiet success: an orphan
  draft may exist and no later step can prove which release is this run's. The reason this survived
  two review rounds is the harness: its stub `gh` swallowed `--json`/`--jq` and printed an id. The
  stub now enforces the documented flag set, and the harness runs v0.12.10's publish step against it
  and watches it fail. No tag has run the gate: still configured, not proven.
- v0.12.12 — the rest of the third review's findings, one of which was about the harness rather than
  the workflow. The gate now compares a third property of every published asset: the digest GitHub
  reports for the bytes it stored, against the staged file's own sha256. Name, `uploaded` state and
  byte size together admitted an equal-size substitution — the pre-fix comparison is now a standing
  harness case that is shown accepting a stored asset of exactly the right size and the wrong bytes.
  An asset whose digest the remote does not report fails the gate too: an unverifiable field is not a
  passing one. The archive member test asks for the member's *type* as well as its name, because
  `tar -tzf` lists names only and a symlink member named `air.exe` satisfied the name test while
  unpacking into nothing runnable; the verbose listing's type character must be a regular file. Two
  further findings are written down rather than papered over. The gate still never downloads the
  served bytes and hashes them itself: it proves what GitHub says it stored, not what a downloader
  receives, and that is the accepted gap recorded in §15. And the workspace-internal tricks (a symlink
  or hardlink above the leg folder, PAX extension oddities, a decompression bound) plus the window
  between re-resolving the tag and creating the release are outside what a workflow can decide: the
  tag-scoped `concurrency` group already serializes this repo's own runs, and excluding an external
  writer is a repository-settings decision, not a line of YAML.
- v0.12.13 — a fourth deep review blocked the first tag on a claim v0.12.11 repeated: the release id was
  read back from `GET /releases/tags/{tag}`, and the API description for that endpoint is "Get a published
  release with the specified tag" — a release created as a draft is not findable by tag (drafts are listed
  only to a token with push access). The step failed closed, so nothing wrong was ever published; the gate
  simply could not publish at all. Create is now `POST /releases` (`-f tag_name`, `-F draft=true`,
  `-F prerelease=…`, `-F generate_release_notes=true`, typed with `-F` because `-f` sends strings), and the
  id and `upload_url` come from that response: the id is the one the create handed this run, not a claim
  about who owns the tag, which is also what cleanup deletes. Assets upload through that `upload_url`
  addressed by release id (`gh api --input` with an octet-stream header; gh does not expand URI templates —
  a literal `{?per_page}` is sent as-is and 404s — so the template part is cut and the name goes in the
  query). The draft is inspected from **one** asset-list snapshot rather than five per-name lookups, which
  were five different moments on the server, and that inspection runs twice: after upload and immediately
  before the un-draft `PATCH`, with the tag re-resolved at that second point too. The harness stub now
  refuses `releases/tags/` outright and shows v0.12.12's publish step failing, next to cases for a create
  that answers no `upload_url`, an upload that dies or answers no asset id, an asset name listed twice on
  the release, and a tag that moves after the first resolve. No tag has run the gate: still configured,
  not proven.
- v0.12.122 — authenticate bounded offline commission intake before supervised launch construction (§5.3.1).
- v0.12.121 — refuse an explicit context minimum before queueing when every declaring engine
  has configured serving tiers and none fits. Keep estimate-based admission (§5.5, §6.1, §7.6).
- v0.12.120 — retire each model queue when its last member leaves, remove its status row,
  and give a recreated queue a new equal-depth position (§5.5, §6, Decision 80).
- v0.12.119 — acknowledge explicit admin switches with a queryable operation ID and retain their
  structured outcomes on the serving router (§§5.5, 6).
- v0.12.118 — generate bounded offline tuning workload manifests from configured serving tiers (§11.2).
- v0.12.117 — Document the operator dashboard's current admin reads, loopback access and retained feed semantics (§§6, 10, 12.5).
- v0.12.116 — declare seven MiniMax M3.1 Flash context tiers on the shared external Maya listener,
  with explicit 32k fallback, 64k lowest normal selection and nominal 96k default (§7.6).
- v0.12.115 — decline Windows Docker named-pipe transport and refuse router-owned pipe addresses
  before provider binding or I/O (§5.3.1, §7, §15).
- v0.12.114 — report core shutdown on a writable default HTTP stream with one in-band
  `cancelled` error and `[DONE]` when the producer's armed-read observations attribute the
  result to core shutdown and the acknowledged prefix permits a terminal (§6).
- v0.12.113 — anchor the exact supervised Docker container ID and commissioned API before start.
  Preserve that closed operation across a lost start acknowledgment or disputed journal persistence (§5.3.1).
- v0.12.112 — require exact Docker observations for coordinated starts acknowledged with `204`,
  before the first readiness proof and on idle health ticks (§5.3.1).
- v0.12.111 — compare two saved short-grid tuning summaries offline with exact same-concurrency
  matching, bounded versioned inputs and descriptor-based file admission on Linux x64 (§11.1).
- v0.12.110 — add internal native Docker exec and finite log capture with bounded non-TTY framing,
  exact-ID ownership and independently inspected exec exit (§5.3.1).
- v0.12.109 — apply the counted ingress body cap to `POST /admin/threads` and `POST /admin/switch`.
  Both use the existing lowercase 413 before JSON parsing or side effects. The admin wire tests cover
  declared and chunked overflow, valid requests at the cap and malformed JSON. §10 explains why the ingress
  cap supplies no serialized ticket snapshot ceiling and records the full identities returned by that surface.
- v0.12.108 — produce and recover an offline supervised initial-launch receipt for the checked
  DeepSeek/Qwen pair. Anchor full commissioning through issue and require independent wire and
  effective-state evidence before recording an applied launch with ingress closed (§5.3.1).
- v0.12.107 — report model and engine identifiers at reporting length in SSE keepalive comments.
  Specify encoded byte ceilings for keepalives, HTTP error JSON and the committed-stream error
  and completion pair (§6, §6.1).
- v0.12.106 — latch the exact failed native switch target across dispatch wakes and unrelated
  reloads. Allow an authenticated, explicitly enabled reset of one latch (§5.3.1, §6, §7.7).
- v0.12.105 — fence native Docker stop/removal uncertainty in the live provider and order
  incumbent restoration against exact stop/removal issue on that provider (§5.3.1).
- v0.12.104 — add independent opt-in SSE buffer and upstream idle limits for HTTP completions.
  Pause upstream reads at exhausted output credit, abort a continuously blocked write after 30 s,
  and separate HTTP final-flush acknowledgment from upstream transport cleanup (§5.5, §6, §7, §14.1).
- v0.12.103 — bound the upstream non-2xx diagnostic read to the first 256 body bytes
  or earlier EOF. Keep the existing error excerpt and apply the response deadline to that
  read when the gate is armed (§5.5, §10).
- v0.12.102 — reload validated defaults and, at router-wide work idle, external engine membership
  through an authenticated file-backed apply. Keep admitted streams and switches on their starting
  configuration, keep detached inference counted through cleanup, and preserve the current
  generation on refusal (§5.5, §6, §7.7).
- v0.12.101 — verify an exact owned container's exit or absence after a native Docker stop
  conflict before allowing replacement. Cleanup after a verified exit uses non-force removal
  and conflict-derived observations do not train shutdown timing (§5.3.1).
- v0.12.100 — relay complete SSE blocks byte-for-byte and discard an unfinished block before a terminal
  error. Apply the same block-boundary check after an upstream-only read cancellation. Bound one block
  to 1 MiB before its delimiter. HTTP keepalives continue until a complete block or terminal result,
  and the response deadline still ends at the first positive raw read (§5.5, §6).
- v0.12.99 — verify the selected enrolled pair against an independent current topology head
  and freeze an inert offline descriptor (§5.3.1).
- v0.12.98 — document seven HY4 Flash context targets on the shared external Maya listener and
  pin ordinary routing, observed capacity and offline launch projection (§7.6).
- v0.12.97 — exercise the dedicated DeepSeek and Strata Qwen swap in both directions offline,
  including certified delivery, stale route refusal and independent probe readback (§5.3.1).
- v0.12.96 — add the offline coordinator's enrolled receipt-only launch and recovery, and
  record the dedicated-inference deployment assumption (§5.3, §5.3.1).
- v0.12.95 — recover one synthetic initial launch receipt bound to an independently approved
  Docker create payload and a modeled journal head, and verify closed ingress and completion cuts (§5.3.1).
- v0.12.94 — keep named option operands out of the `air chat` and `air complete` user message,
  read stdin when no positional arguments remain, and document the direct HTTP client (§11).
- v0.12.93 — project matching TCP exposure for published ports on engines with per-tier Docker
  plans or engine-wide offline launch options (§7).
- v0.12.92 — project tier-specific Docker shared memory for twelve default HyperQwen parking
  targets offline, with common shared memory inherited on omission (§7).
- v0.12.91 — project an explicit single-device NVIDIA request per offline serving tier for
  six HyperQwen targets, with ordered common GPU selection inherited on omission (§7).
- v0.12.90 — the offline immediate-host callback orders host acceptance, completion and
  returned refusal with shutdown (§5.3.1).
- v0.12.89 — engine shutdown withdraws current health and readiness even when idle and retains
  the last observed model roster (§5.5).
- v0.12.88 — project Docker init, unless-stopped restart, daemon-default log rotation and matching
  exposed ports offline for eight paired-GPU HyperQwen targets (§7).
- v0.12.87 — offline Docker serving tiers can replace the common container command with literal
  arguments. General DeepSeek projects eight numeric context arguments without changing tier names (§7).
- v0.12.86 — qualify original-launch deadline equality, exact proof binding and guarded Open in
  the offline enrolled host fixture (§5.3.1, TASK-110, TASK-126).
- v0.12.85 — offline Docker serving tiers support explicit read-only nested binds and checked
  health probe timeout and retries. The Maya-XS 32k projection remains behind the tier gate (§7).
- v0.12.84 — offline Docker serving tiers accept profile aliases that resolve to the same
  canonical launch plan. Default tiers and activation gates retain their existing rules (§7).
- v0.12.83 — bind an offline admitted ticket's effective launch intent, final JSON and upstream
  credential policy to its verified resident and single-use stream. Separate logical outcome
  from issuer retirement, keep unsettled physical use counted and report independent
  cleanup faults. Caller cancellation interrupts the pending header read (§5.3.1, §5.5, §7.6).
- v0.12.82 — qualify closed-route staging through verified publication refusal, drain disposal
  and explicit fresh-generation open in the offline host coordinator fixture (§5.3.1, TASK-110).
- v0.12.81 — offline Docker create projection supports literal serving-tier health checks and
  checked Docker timing fields. Health-only tiers keep the native startup and mutation gate (§5.3.1, §7).
- v0.12.80 — offline Docker create projection supports a literal image entrypoint and host IPC
  for a native engine. Either declaration gates engine startup and Docker mutations (§5.3.1, §7).

- v0.12.79 — Exercise closed-generation physical retirement across controller death in the
  offline host fixture. The exact backend terminal and successful write seal both precede a
  successor stop (§5.3.1, TASK-110).
- v0.12.78 — offline Docker create projection resolves memory and total memory plus swap per
  serving tier. The two values inherit or replace as a pair, and target-enabled mutation remains
  gated (§5.3.1, §7).

- v0.12.77 — qualify offline one-shot transport over a host-issued, preauthenticated serving
  stream. First write requires the exact issued grant and its verified route and invocation.
  The single-host issuer refuses reuse, and the exchange retains its permit through
  issuer-confirmed cleanup (§5.3.1, TASK-130).
- v0.12.76 — Qualify independent partition vacancy and owner evidence for the offline
  successor fixture. Prepared operations requery after restart and uncertain dispatch stays fenced
  (§5.3.1, TASK-110).
- v0.12.75 — preserve the exact ticket's explicit context minimum through dispatch and
  post-launch model readiness. Joined waiters apply their own minimum to the observed
  window. Bound an idle switch claim to eight superseded capability observations and
  the caller's original wait budget, including initial lock acquisition. An injected
  host switch requiring measured capacity refuses before host effects (§5.5, §7.6, TASK-68).
- v0.12.74 — native Docker configuration resolves per-serving-profile environment and mounts for
  offline create projection (§5.3.1, §7). Canonical model and profile select immutable launch
  inputs. Engines with these declarations require separate host qualification before activation.
- v0.12.73 — Add the standalone offline host successor checkpoint and effect ledger.
  The checkpoint retains exact launch, stop and frozen successor evidence across process death.
  The fake ledger verifies residency and separates send, acceptance, attempt and applied records
  across restart without granting production host authority (§5.3.1, TASK-110).
- v0.12.72 — qualify exact-ticket admission with an offline sender and bounded preparation.
  A retirement wake revisits deferred staged preparation before an ordinary route can reserve
  the freed slot. Model and profile routes share the engine's physical permit budget. Repeated
  aborts share retirement completion, and a submitted use stays counted if disposal fails.
  A dispatcher entering its wait during teardown uses the router's captured shutdown token
  (§5.5, TASK-64).
- v0.12.71 — order local inference sends and each health read against engine shutdown.
  A timed-out send retains its client through late response cleanup. Shutdown attempts
  lifecycle cleanup even if local HTTP close faults. Switch readiness delivery and timing
  proof remain separate from local send permission (§5.5).
- v0.12.70 — both non-enrolled switch paths use one monotonic post-launch deadline through
  complete observations and guarded settlement. Expired or retired reads release their I/O
  without publishing. The terminal decision retains a completed in-budget failure diagnosis.
  A joined caller sees its committed outcome after event delivery, including when the caller's
  wait expires or engine shutdown wakes it (§5.5, TASK-126).
- v0.12.69 — read-only switch refusal restores an incumbent's serving tuple only while its exact
  claim has no serving contradiction. Late evidence from that claim can first withdraw serving
  capacity and then withdraw roster health on a model contradiction (§5.5, TASK-68).
- v0.12.68 — The offline shared-host fixture persists dispatch and worker acceptance before create.
  After a lost reply, a new process uses the durable receipt to verify the exact instance without a
  second create. An accepted operation without a terminal receipt fences successor mutations.
  Production host enrollment remains disabled (§5.3.1, TASK-110).
- v0.12.67 — the built-in dispatcher compares observed shutdown and activation costs for
  competing switches on one idle native Docker engine with a proven incumbent. Aged eligible
  tickets take precedence within the comparison. Shutdown cost requires matching launch, stop
  and incumbent sample identities (§5.5).
- v0.12.66 — ordinary model and serving-state probes publish one versioned capability. A profile
  needs a current window bound to its model. Missing state withdraws capacity without withdrawing
  a healthy roster (§5.5, §7.6, TASK-68).
- v0.12.65 — load typed native Docker resources and verify the exact create payload offline.
  Docker resource settings share `HostConfig` with networking. Resource-bearing lifecycle
  mutations remain gated on host and serving-instance qualification (§5.3.1, §7).
- v0.12.64 — `EngineRuntime` rejects a supplied Docker provider whose engine name, router ID,
  or configured daemon differs from the engine definition. The provider uses the same captured
  scope for its client and container labels. External engines leave supplied providers unused
  (§5.3, §5.3.1, TASK-128).
- v0.12.63 — the SSE relay keeps one heartbeat cadence across admission and the wait for the
  first upstream byte. Buffered data and terminal results take precedence over a due heartbeat
  (§6, TASK-127).
- v0.12.62 — the offline release harness now distinguishes the old 200-byte whole-summary cut
  from the fixed diagnostic. The same 216-byte summary loses its URL tail on `79a21e0` and
  must retain it on the current workflow. A 198-byte control retains the tail on `79a21e0`.
  The three field-type cases still pin the separate rendering fix (§15, TASK-44).
- v0.12.61 — Add the shared-host reservation and an internal runtime path for fake-controller
  qualification. The coordinator persists mutation intent before dispatch and holds the reservation
  through guarded proof publication and switch settlement. Production host enrollment remains gated
  (§5.3.1, TASK-110).
- v0.12.60 — dispatch evaluates and takes the same thread-admissible ticket, restarting the
  queue scan if that ticket loses its place before selection commits (§5.5).
- v0.12.59 — Only an exception from the ordinary `/models` fetch can withdraw probe health.
  An ordinary poll preserves a switch's proof while its marker is raised. A failed mutation clears
  claim authorship in the unknown snapshot, one publication before marker clear (§5.5, TASK-76).
- v0.12.58 — native Docker preflight checks the image ID, named repository digest and platform
  before it stops owned containers. Create uses the full pinned ID without a tag fallback.
  Legacy files still load. Native switches require the pins (§5.3.1, §7, TASK-26).
- v0.12.57 — Inference reach and lifecycle authority are independent engine settings. Legacy
  configurations normalize at startup, and a bound provider must match the configured kind.
  Engines without authority still forward loaded models. A verified refusal before mutation can
  retain the incumbent's discovery proof, including per-model context windows. Explicit native
  authority is limited to a dedicated local engine until host fencing is available (§5.3, §5.5, §7, TASK-77).
- v0.12.56 — `GET /v1/models` can report the observed context window for each loaded model.
  Roster and windows come from one engine observation. Unknown windows and ambiguous model
  identities omit the field. Public capability array signatures remain compatible (§6, TASK-88).
- v0.12.55 — `air chat` and `air complete` exit unsuccessfully on orderly stream EOF before
  `[DONE]`, keeping partial stdout and reporting early EOF once when the stream supplied no error
  (§11, TASK-108). Process tests cover both verbs and both output modes.
- v0.12.54 — the managed switch walkthrough uses the native Docker provider (§5.3, §5.3.1,
  §7 and §7.6). It documents the shipped ownership and readiness checks, the legacy exec
  path, and the target rules for host exclusivity, failure quarantine and alternate recovery.
  The CLI synopsis uses the shipped `air` tool (§11), and the Docker walkthrough explains the
  port conflict that can surface after the incumbent stops.
- v0.12.53 — an expired switch wait checks the shutdown latch after reading a prior outcome
  (§5.5, TASK-98). A foreign attempt that already finished does not hide a stop. A caller retains
  its own committed failure or a readiness proof that still stands. The public switching flag
  reads the scheduler's capability marker.
- v0.12.52 — release preflight validates the entire manual tag input as data and always forces
  dry run for manual dispatch (TASK-90). The preflight lookup and both publication rechecks resolve
  the full `refs/tags/<tag>` reference. The offline harness rejects the short `tags/<tag>` form with
  HTTP 422, compares historical branch-only acceptance with the current refusal, and checks both
  publish lookup paths. The CI job
  `release gate harness (release.yml step bodies)` runs 137 cases (§15).
- v0.12.51 — completion streams carry router keepalives as SSE comments (§6, TASK-108). The
  strict-parser pin rejected the original writer at the first keepalive and accepts the same upstream
  completion with comment framing. Status is a JSON string on one comment line. The CLI shows status
  changes on stderr in text mode, retains data payloads in JSON mode, and exits unsuccessfully on a
  streamed error. Byte-level pins cover upstream frames, the queued sequence, and one error followed by
  the existing sentinel on pre-sentinel failure.
- v0.12.50 — shutdown and switch completion share one transition gate (§5.5, TASK-98). The first published
  outcome is permanent. Shutdown records `RouterStopped` before waking waiters or cancelling the switch token,
  and a caller that reaches the claim gate after that decision is refused. `ShutdownStopCauseTests` pins both
  boundaries and covers readiness proofs on either side of shutdown. The request path maps the typed outcome to
  the shipped `cancelled` (499) response. TASK-81 owns the wire and lifecycle reporting changes.
- v0.12.49 — a feed subscriber's exception cannot reach the code that emitted the event (§6.1, TASK-103 #4).
  `EventRing.Append` invoked every subscriber inside the append with no isolation, so an SSE handler whose client had
  hung up threw inside a capability publication, a switch ending, or a terminal settle — a stranger's bug answering a
  caller. Dispatch is now per-subscriber and isolated: the append completes and keeps its sequence number, the
  subscribers that did not throw still receive the event, the emitter finishes its transition, and the fault is
  counted (`EventRing.SubscriberFaults`, surfaced on `/admin/status` as `FeedSubscriberFaults`). Four pins in
  `SubscriberFaultTests`: the ring half, the producer half (a subscriber that throws on **every** event leaves the
  caller's answer `ready` and the capability a proof), the stranding half (a throw at the `engine.switch_started` emit
  leaves no waiter parked: both callers answer, the marker clears), and the visibility half (the count read back over
  real HTTP). **Control:** reducing the catch to counting-and-rethrowing reddens all four named pins
  (`Failed: 4, Passed: 0`), and the tree was restored byte-identical (`git diff | sha256sum`
  `a8d0d50c…c3c6e218` before and after); the pre-fix tree is not usable as the control here, because the pins name the
  counter and that tree does not compile them. Measured at this tree: `SubscriberFaultTests` + `EventRingTests`
  19/19, full suite 235/235, `dotnet build` 0 warnings.
- v0.12.48 — a caller that arrives at a switching engine joins it before any catalog question is asked of it (§5.5, decision-51, TASK-94 #14).
  Both catalog guards sat ahead of the claim loop, so they answered a caller that had come to **join** a switch already
  running. That is not cosmetic: a refusal is terminal, so the joiner never learned what that switch decided. Measured
  on the pre-fix tree (`d9b61f1`, detached worktree, the new test file copied in), a caller joining a switch claimed on
  a usable entry — the entry replaced mid-switch — answered `no_select_command` where the switch it was parked behind
  answered `launch_failed` (`Expected: "launch_failed" / Actual: "no_select_command"`): it told the caller to fix a
  configuration that had governed nothing, instead of telling it that the transaction it was waiting behind had failed
  and could be retried. The order is
  now fixed under the transition gate: read the switch state; a running switch is joined with no catalog read at all;
  the catalog is asked once per acquisition attempt, and only of a caller the idle engine hands to the claim path — a
  lost capability exchange re-reads capability **and** entry. A refused attempt publishes nothing and writes no state,
  and that is read at the seam (`AfterClaimForTests` fires zero times for it), not inferred from an absent
  `switch_started`; the refusal is emitted after the switch lock is released, so no feed subscriber runs holding an
  engine lock (TASK-103 #4). A caller that joins another request's switch, is overtaken by its failure and re-loops is
  refused **there**, as the acquirer it has become — asserted as correct rather than special-cased away. A budget that
  expires while a caller is joined stays a timeout. Four pins, in `SwitchJoinTests` (its own class, because the
  standing rule keeps new parked-launch pins out of the two classes TASK-87 is measuring):
  `AJoinerWhoseEntryVanishedMidSwitchIsAnsweredByTheSwitchItJoined`, `AJoinerIsToldWhatTheFailedSwitchDidNotTheCatalogItCannotRead`,
  `AJoinerThatBecomesTheAcquirerIsRefusedByItsOwnAbsentEntry`, `AJoinerOutOfItsOwnCeilingIsTimedOutNotRefused`. All four
  red at `d9b61f1` — three at "the joiner was still parked", one at the ceiling sentence — and green here; that pre-fix
  tree is the control, and it reddens the four named pins. Measured at this tree: Debug 231/231, Release 231/231,
  `dotnet build` 0 warnings, AOT publish with `TrimmerSingleWarn=false` 0 warnings.
- v0.12.47 — a refusal that costs the engine nothing is pinned as one published value, not as a health flag (§5.5, TASK-94 #10).
  `ABlankSelectorExecIsRefusedBeforeTheClaimToo` asserted `Healthy` after the refusal, and `Healthy` is true of a
  *replaced* snapshot: a refusal that claimed a switch and rolled it back, or published a fresh list for the entry it
  had just refused, left that pin green while burning the engine's version. The pin now captures the snapshot before
  the call and compares the one value the promise is about — same `Version`, same health, same reported list, the list
  compared as its contents rather than its instance — with the version doing the work, because every publication
  increments it and a quiet poll that learns nothing leaves it alone. **Control, run against this tree and reverted
  byte-identical afterwards:** bumping the published version at the refusal while holding health and the list identical
  reddens that pin alone (`Expected: 1 / Actual: 2` at the version assertion), which is the assertion the previous pin
  did not have. Test-only: no production line changed; `RefusalCategoryTests` 12/12 in Debug at this tree, and the full
  suite is CI's gate.
- v0.12.46 — two refusal pins now prove the thing they claim, and one claim of mine is withdrawn (§5.5, TASK-94 #9/#10).
  `ABlankSelectorExecIsRefusedBeforeTheClaimToo` arms the seam at the claim itself and requires **zero** fires, which is
  what separates a refusal in front of the transaction from one a step behind the marker: before this it proved only
  that the provider was never asked, and a post-claim refusal would have satisfied that. It also reads both halves at
  their own seam — the caller's bare name, the feed's actionable sentence — and keeps the last-proof assertion.
  `ADefinitionSwappedAfterTheClaimStillRunsOnItsSnapshot` stopped counting launches, because a launch count of one is
  the same answer for the claim's snapshot and for a re-read: both routes launch once. It asserts the selector the
  provider was **handed** — the snapshot carries the exec the claim was authorised on, the replaced config entry carries
  a blank one. **Controls, both run against this tree and reverted byte-identical afterwards:** dropping the
  unusable-selector clause reddens the armed-witness pin, the catalog-miss pin and the divergent-definition pin (4 cases);
  making the switch re-read the live config instead of the claim's entry reddens the swap pin alone. An earlier control
  attempt of mine — prefixing the condition with `false ||` — left the clause intact, reddened nothing, and is withdrawn:
  it tested nothing. The whole suite 227/227 in Debug, build 0 warnings.
- v0.12.45 — the claim's catalog entry is part of the snapshot, and the mutation policy is stated where it can be read (§5.5, TASK-94 #9–#13).
  v0.12.44 unified the definition object but left each side doing its own `Catalog.TryGetValue` at a different moment —
  two asks of a mutable dictionary, the same mistake one level down, and it made §5.5's "one predicate, asked once" true
  of the definition only. The target's `EngineModelDefinition` is now resolved before the claim marker goes up and
  carried into the switch; the post-claim missing-entry branch is deleted rather than re-guarded, so an absent entry is
  refused in front of the transaction like a blank selector is — no marker, no publication, last proof intact. The read
  fixes the policy too: a configuration edit landing after it changes no already-claimed switch. Three pins:
  `ADefinitionSwappedAfterTheClaimStillRunsOnItsSnapshot` (the config entry replaced from `AfterClaimForTests`
  mid-switch — the transaction still launches and is not re-decided), `AUsableConfigDefinitionIsNotRefusedByABlankConstructedOne`
  (the other divergence direction: a runnable config entry is not refused on a stale constructed blank one), and the
  divergent-definition pin extended to require the last proof standing afterwards. The v0.12.44 row's sentence "the check
  and the transaction cannot disagree" was true of the definition and not of the entry, and is corrected in place here.
  Measured in this checkout: build 0 warnings; RefusalCategory 12/12; the switch/refusal/contract/end-to-end filter 69/69;
  whole suite 227/227 in Debug; AOT publish trim-silent.
- v0.12.44 — one definition decides a switch, from the refusal to the transaction (§5.5). The commit review of v0.12.42
  had removed the selector ask inside `RunSwitchAsync` as unreachable; it was reachable, because the check in front of
  the claim read the definition the runtime was constructed with and the switch re-read the config collection by name.
  A same-name definition handed to the public constructor, or an entry swapped between the two reads, therefore put a
  runnable selector in front of the claim and a blank one inside the transaction, and answered `launch_failed` after
  throwing away the engine's last proof — the exact cost the refusal exists to avoid. The claim site now resolves one
  snapshot (`_config.Engines[name]`, falling back to the constructed definition) and passes it to the switch, so the
  check and the transaction read one definition object — **though each still looked the model up in it separately,
  which v0.12.45 closes**; a second ask would not have fixed it, since it would still read a second source. `ADivergentDefinitionIsRefusedFromTheSnapshotTheSwitchWouldUse` pins both provider shapes; measured red 2/2 on
  `main` at `2f7e3e6` and green 2/2 here, with the whole suite 225/225 in Debug and the build at 0 warnings. §5.5 and the
  v0.12.43 row are corrected where they asserted the deleted branch was unreachable, and the set claim is narrowed to
  what the type actually guarantees: no caller-facing route to editing it, not a set that nothing in the assembly can write.
- v0.12.43 — the refusal set cannot be edited, the pins read the whole answer, and SPEC stops naming the wrong arm (§5.5, §6.1).
  The commit review of v0.12.42 found three claims that the code did not support, and all three are in this row. §5.5 had the
  shutdown answer arriving at the caller as `engine_unreachable`; it does not — the resume path throws cancellation, the
  request path's `OperationCanceledException` arm answers `cancelled` / 499 with no model named, and the
  `engine_unreachable` catch is a different arm. The closed set was declared as a `readonly` array, so any code in the
  assembly could rewrite its elements and the "closed" half of "closed set" was a comment; it is a `ReadOnlySpan`, and the
  door's rejection message no longer quotes the name it rejected, because that message can reach a caller. The refusal
  walk read the caller's token by cutting at the last `": "` — a detail of `anything: refresh_omits` passed it — so the walk
  asserts `Detail` whole. The second ask of the selector predicate inside `RunSwitchAsync` was deleted here as
  unreachable — **that premise was false**, and v0.12.44 restores the guarantee a different way (one definition
  snapshot; see §5.5 and the v0.12.44 row). The case that could actually drift is pinned here: an entry whose `select.exec` is blank is refused `no_select_command`
  **before** the claim, the provider is never asked, no `engine.switch_started` reaches the feed, and the engine keeps its
  proof. Measured in this seat's checkout, in the tree committed: build 0 warnings, whole suite 223/223 unfiltered in
  Debug, and the focused filter 93/94 once with the one failure named —
  `SwitchClaimTests.AWaiterInsideTheReadinessWaitIsToldTheRouterStopped` at its 30 s ceiling, which is TASK-87's flake,
  red on `main` at v0.12.42 as well and not this change. Two controls, each reverted and the working diff's sha
  re-checked byte-identical: narrowing the selector predicate to a null check reddens
  `ABlankSelectorExecIsRefusedBeforeTheClaimToo` and nothing else; putting a quoted-name message back in the door
  reddens `AProseCategoryCannotBeBuiltIntoARefusal` and nothing else.
- v0.12.42 — every switch refusal answers with one name, and the set is enforced where the answer is built (§5.5, §6.1, TASK-94).
  The closed set has **one owner** — `SwitchReason.All`, in `WireText.cs` — and eight members: `no_select_command`,
  `launch_failed`, `health_timeout`, `refresh_omits`, `refresh_unanswered`, `window_too_small`, `switch_error`,
  `no_lifecycle_owner`. Two exits that answered in prose answer with a name now. A router that owns no loading authority
  over an engine says `no_lifecycle_owner`: the token names the **relation**, not the `remote` flag that answers it
  today, so the reach/ownership split (TASK-77) will not cost a second wire break. A switch task that never started
  says `switch_error`, the catch-all, and no longer names the model of the switch it could not start — another
  request's identifier sitting inside this caller's error body. **The set is a door, not a list:**
  `EngineRuntime.SwitchResult.Failed` refuses any name outside the array, `FailedFromRecord` is the single place an
  attempt's recorded reason becomes a caller-facing answer and the single place the catch-all is applied, and the
  resume exit stopped wrapping the token in `switch to <target> failed:` — prose that restated the target the caller
  had just named. **A phase is not a cause, and a wait is not an attempt:** `switch_not_started` is feed-only and
  deliberately outside the caller's set, and that record is written **before** the switch is ended, with the end in a
  `finally`, because `EventRing.Append` runs every subscriber inside its lock with no isolation (TASK-103 owns the
  general rule) — an observer that throws must not be able to latch the engine mid-switch, and no waiter may wake onto
  a refusal whose explanation is not on the feed yet. An **expired wait** carries no name at all: the attempt it was
  parked behind is still running, so the set (which says why an attempt *stopped*) is scoped to completed attempts and
  the caller keeps their own ceiling in milliseconds. **The readiness window names what its last poll saw:** the three
  ways a `wait_health = true` switch ends unsatisfied are three refusals — `health_timeout` (retry later),
  `refresh_omits` (the engine stayed up and never named the target, so the catalog key is wrong) and `window_too_small`
  (a ladder tier, not a retry) — where v0.12.41 named all three `health_timeout`, a permanent mismatch wearing a
  transient name; the window and its milliseconds stay on the feed at all three. A catalog entry that names no selector
  is answered `no_select_command` **before** the transaction by the same predicate preflight asks, instead of coming
  back as `launch_failed` after the engine's last proof had been thrown away for a transaction that never began.
  §6.1's caller contract is the **bare eight names in parentheses, no parameters**: the parameterised feed phrases it
  listed before (`refresh_omits_<model>`, `health_timeout after <ms>ms`, `window_<n>_lt_<max>`) were operator text
  presented as caller tokens, which made a caller parse prose it did not own; `code` stays `switch_failed` at 503,
  and `docs/ai-integration.md` carries the same table for integrators. **Evidence, run in this seat's checkout:**
  build 0 warnings; the new `RefusalCategoryTests` walks eleven constructed exits against literals written in the test
  itself, asserts set equality in both directions, audits the engine's source for every failed `return`, for the single
  line allowed to construct a `Failed` outcome, and for the one arm that applies the catch-all, and pins the
  feed-before-end ordering from inside the subscriber; it is a new class rather than new test methods in the two
  classes TASK-87 keeps closed to new assertions — there, `SwitchClaimTests` has two existing assertions amended to the
  bare name the exit now answers, and `CapabilityPublicationTests` has the category of one block amended and the walk's
  closed-set count amended (7 → 6, because two of its blocks now answer the same name from two paths). Focused filter
  (SwitchClaim, CapabilityPublication, RefusalCategory, ErrorContract, EventRing, StreamFrameIdentity,
  RequestPhaseTimeout, EndToEnd) **93/93**; whole suite **222/222 in Debug and 222/222 in Release**, against the 215
  the v0.12.41 row recorded; AOT publish with `TrimmerSingleWarn=false` silent. **Six mutations**, each reverted and
  the working diff's sha re-checked byte-identically afterwards: dropping `NoLifecycleOwner` from the array reddens the
  set walk and the audit; answering the refresh-omits exit with prose reddens nine cases across the two classes — the
  source audit, the exit walk, the expired-wait pin, and five `CapabilityPublicationTests` that read the caller's half;
  swapping the readiness classification in either arm reddens the per-exit pins and
  the same-name-two-paths pin while the set equality stays green; leaving `health_timeout` with no producer reddens the
  audit; moving the never-started record after `EndSwitch` reddens the ordering pin alone; and building a `Failed`
  outcome directly at the resume exit reddens the audit's single-construction rule and
  `ALaunchThatFailsOnItsOwnIsStillToldAsAFailedSwitch`. **Deliberately left open, recorded as gaps in §15:**
  `launch_failed` still absorbs a failed stop or a failed ownership check inside one transaction (the reach/ownership
  split is TASK-77), and `POST /admin/switch` still answers `202` plus a feed account rather than a category, because
  it starts a switch it never waits for.
- v0.12.41 — the relay's byte promise is pinned at the byte level, not asserted around (§6). `StreamFrameIdentityTests` replays one
  SSE fixture through a live `ServerApp` at **every** byte offset of a short fixture and at all 64 alignments of a 27.6 KB one,
  and compares the client's bytes with the upstream's after stripping only the **contiguous** keepalive prefix — so a torn frame,
  a repeated chunk, or a keepalive inserted after the first upstream byte fails byte equality instead of reading as a slow
  stream. The fixture carries `é` and `🌲` (multi-byte characters astride the split), an upstream comment line, an unknown `id:`
  field and `[DONE]`; the third case holds the only engine slot with a first request so a queued stream really does emit
  `seq=0/1/2` before an upstream frame arrives in two reads, and it accepts only complete, consecutive keepalives — no sleeps
  anywhere. Reviewed and re-run by this seat rather than trusted: build 0 warnings, the filter 3/3 on three consecutive runs,
  the whole suite 215/215, and two relay mutations of this seat's own each reddening all three pins — dropping the last byte of
  every chunk the relay copies, and inserting a keepalive after every chunk (`ServerApp.cs:221-227`). `WireHarness.StartAsync`
  gained an optional `fake:` argument so a test can script upstream reads without a second harness. Still open on that row: an
  empty `data:` frame in the fixture, and the truncation / mid-frame-disconnect case whose terminal error body §6.1 specifies
  (existing `StreamRelayTests` only asserts an absent `truncated` frame).
- v0.12.40 — a caller's error body is bounded where the router ends a request, not where a writer happens to remember (§10, §6).
  The v0.12.39 funnel capped the **feed**, and §10 went on claiming the same was true of a caller's error body; reading the
  exits found that it was not. `Fail` and `RejectAtAdmission` stored the sentence and threw it unbounded, the queue-cap refusal
  quoted a model id out of a request body whole, and the HTTP error writer forwarded an exception message it had never
  measured — so `ticket.Error`, the `/v1` body, a stream's error frame and `GET /admin/tickets/{id}` each carried text whose
  length somebody upstream chose. Both terminal paths now cap the **finished sentence** at the 1600-character ceiling (one
  string, so the caller's answer and the feed's reason cannot drift), the queue sentence cuts the id before interpolation, and
  the writer caps what it serialises — which is the layer that owns an admin refusal quoting a rejected profile name or an
  unknown ticket id. The admin snapshot keeps `Id`, `ThreadId` and `Model` **whole** on purpose: the reporting caps bind
  excerpts and prose, not the identities an operator acts on, and that response's size is bounded by `max_request_body_bytes`.
  §10 also stops claiming the cut marker is an identity: it is 32 bits of folded FNV-1a, a diagnostic distinguisher with about
  one colliding pair in 4.3 billion, and a value written literally as another id's head-plus-marker passes the cap unchanged,
  so `thread`/`engine` filters are excerpt matching and `ticket` stays exact (decision-45). The server assembly shares the
  library's `WireText` through `InternalsVisibleTo` instead of keeping a second mechanism with its own numbers.
  Five pins: two over real HTTP (a 5,000-byte upstream error body; a 4,000-character profile name in an admin refusal — each
  asserting the status, the one lowercase shape, the message length and a ceiling in **serialized bytes**, because escaping
  makes the body exceed the count) and three at the library level (the queue refusal against its ticket snapshot, an upstream
  exception sentence, and the filter's alias case with an exact-ticket control). The feed pins tightened from
  `IdentifierChars + MarkerReserve` to `IdentifierChars`, since the reserve sits inside the stated length. Cost, read and not
  measured: over-budget values are hashed while the ring lock is held and a filter re-hashes per replayed event — a
  measurement-driven follow-up, first felt by the live view.
- v0.12.39 — every feed field is bounded where the feed is written, not where each producer happens to remember (§10, §5.5).
  The cap that v0.12.37 installed sat on the event's `Model` field and inside the engine's own emitter, so the ceiling §10
  states for one feed reason was untrue of the request path's failure producer: `RouterCore.Fail` builds its sentence by
  interpolating the model id out of a request body and the engine's configured name, and a caller could choose how much text a
  feed record — and their own error body — carried. The ring's single append funnel now caps `Model`, `Engine`, `ThreadId` and
  `Reason`; the request path cuts the identifiers before it builds the sentence; the admission refusal for an unroutable model
  cuts the id it names; and the emitter's second whole-reason cut is gone, because one ceiling with one owner is a bound and two
  owners is a coincidence waiting to drift. `WireText.Bound` gained the three properties its pins had not covered: the cut marker
  carries an eight-hex-character FNV-1a fingerprint of the whole value, so two identifiers that cut to the same head stay
  distinguishable **in the record** (a caller chooses the head of their own id); a cut backs off rather than landing between the
  two halves of a surrogate pair; and the marker's worst case is named — `MarkerReserve = 32` — so the reserve and the marker
  cannot drift apart, and a budget with no room for a head is refused rather than answered with a head of nothing. The marker
  grew, so the head that survives an identifier cut is 96 characters, and what an operator reads in a refusal is now stated as a
  head — 24 characters of each reported name inside a 500-character quoted list — instead of a total that quietly set the head.
  The feed filter cuts its own values, so a caller with a long thread id still finds their records. Pins: `EventRingTests.TheFunnelCapsIdentifiersAndReasonForEveryProducer`;
  `EventRingTests.ARequestPathFailureStoresItsFieldsAtReportingLength` (a 405-character id posted through the request path and read
  back as a stored reason at reporting length, plus two ids sharing a head that stay distinct); and `WireTextBudgetTests` for the
  arithmetic — budget, unpaired surrogates, the marker's worst case, the two-id pair. Mutation probes, each restored and the working
  tree proved byte-identical after: caps removed at the funnel fails the funnel pin alone; the request path's sentences built from the
  raw id and engine name fail the request-path pin; the fingerprint taken out of the marker fails three pins; the surrogate back-off
  removed fails the surrogate pin alone. One fix rode along in the pin rather than in the shipped code: the last
  sub-case of `CapabilityPublicationTests.TheClaimThatWithdrewHealthIsTheOnlyAuthorOfUnknownHealth` (v0.12.38) waited to
  see the claim's marker, which is a window that can close, and it went intermittent under a full-suite load; it waits on the
  gated launch instead, which is the claim itself.
- v0.12.38 — the snapshot says **who made the engine unknown**, and only an observation withdraws a waiter's proof
  (TASK-61 AC #7–#8, §5.5). One `Healthy` flag was carrying two different facts: a claim in flight takes health down
  as the **price of starting a transaction**, and a probe that cannot ask what the engine serves takes it down because
  the router has learned it does not know. The resume rule treats those two differently — the first leaves a waiter's
  own proof standing, the second contradicts it — and the distinction rested on nothing in the snapshot:
  `OwnProofStillStands` asked "healthy, or is a marker up?", so a failed probe followed by a successor's claim read as
  somebody else's transaction and the waiter was answered `Ready` off a proof the engine had just contradicted. The
  capability snapshot now carries one internal, non-positional bit — the claim that raised the current marker authored
  the withdrawal, until any other author supersedes it — true only for a claim that started from a healthy engine, and
  cleared by a positive probe, a switch's proof, a probe negative allowed to publish, a switch exit, and a marker clear.
  `PublishUnknown` returned early when health was already false, which discarded the one publication that separates the
  two accounts; it now publishes a **provenance-only** change and still advances `Version`, because the scan reads the
  version as its "nothing moved" test and this is not nothing moving. The pair of pins: `SwitchClaimTests.AFailedProbeBetweenProofAndClaimWithdrawsTheWaitersOwnProof`
  (a probe negative between the proof and the claim → the waiter pays for its own switch, launches exactly
  `extra, m, extra`) beside the existing `AWaiterHoldingItsOwnProofIsNotQueuedBehindAnotherCallersClaim` (a marker
  alone → `extra, m`, no third transaction for anybody to lose); and `CapabilityPublicationTests.TheClaimThatWithdrewHealthIsTheOnlyAuthorOfUnknownHealth`
  reads the bit at a gated launch (true), after a fenced probe negative (unchanged, version unmoved), across a failed
  exit (false, with two publications where the early return left one) and over a claim that started from an
  already-unknown engine (false). The probe is driven by the health loop rather than a test-only publication seam: the
  failing status is armed only after the proof is published with no switch in flight, so the probe is the only possible
  author of the health the waiter loses, and the fenced case waits on a completed round trip (a second probe request
  starting proves the first answered) rather than on a delay. Mutation probes, each restored and the working tree
  proved byte-identical after: the predicate arm back to `healthy || marker up` fails the first pin alone; a claim that
  always authors the withdrawal fails both new pins. Not closed here, unchanged: authority held across the admission
  handoff stays the accepted gap in §15 (TASK-25 AC #5, TASK-64 AC #3, whose-switch-runs policy in TASK-72).
- v0.12.37 — a refusal answers its caller with a **category**, and the feed keeps the measurements (§5.5, §10).
  Every switch exit records one token from a closed set named in the source — `no_select_command`, `launch_failed`,
  `health_timeout`, `refresh_omits`, `refresh_unanswered`, `window_too_small`, `switch_error` — and the caller's
  failure sentence carries that token, while the readiness window in milliseconds, both sides of a window
  comparison, the names an engine actually reported and a provider's exit status and action line stay on the feed,
  pinned there to exact strings. Caller-facing text keeps only what the reader already holds: the model they asked
  for, the profile they named, the engine's configured name, their own ceiling. Two sentences stopped naming
  somebody else's request — the ceiling sentence and the shutdown sentence both interpolated the target of the switch
  in flight, and the shutdown sentence reaches a caller's error body through the request path's outer catch, so one
  caller's model id was travelling inside another caller's response. `WireText` now owns the character budgets:
  identifiers at 128 characters with the cut marker and one feed reason at 1600, alongside the existing provider
  reason 160, action line 200 and reported-name summary 420. Identifiers are cut **before** the reason sentence is
  built, not after; the health probe's own exception text on `engine.health` is bounded where it is written; and the
  event's `Model` field is stored at reporting length at the ring's single append funnel, because a model id is
  request-side text and nothing in the request path checks its length. The id is still matched whole — the cap is on
  reporting, never on lookup. Only wording moved: no status code, error shape or casing rule changed. Tests:
  `ACallerIsToldACategoryNotAParameterAtEveryRefusalExit` walks all seven exits and reads back both halves, with the
  observed set closed at seven members of the named set; `AWaitersRefusalNeverNamesAnotherCallsTarget` parks one
  caller behind another's switch and asserts the refusal names its own ceiling and no target;
  `ACallerSuppliedModelIdIsCappedBeforeItReachesTheFeed` posts a 405-character id and checks the reason and the
  payload field. Mutation evidence, one pin per mutation: restoring the parameterised `health_timeout after 300ms`
  fails with `Expected: "health_timeout"` / `Actual: "health_timeout after 300ms"`; restoring the target in the
  ceiling sentence fails its `Assert.Contains`; uncapping the id fails its `Assert.DoesNotContain`. Release suite
  197/197; AOT publish with `TrimmerSingleWarn=false` produced native code at 0 warnings.
- v0.12.36 — the switch-claim tests stop betting on the scheduler, and TASK-61 AC #6 is pinned as a launch sequence
  (§5.5). `SwitchClaimTests.TwoCallersWantingDifferentModelsTakeOneSwitchEach` is the case the withdrawn rule produced
  in CI: two callers wanting different models on a one-model engine. The ordering is made rather than hoped for — the
  first caller is held in the seam between its readiness wake-up and its snapshot read while its own switch has already
  committed and left the state idle, the successor then arrives and claims for its own target, and that load stays on
  its gate while the first caller answers — so the marker the first caller reads is one it did not raise, and its answer
  is asserted while the successor's load is still held. What is asserted is the sequence, not a clock: launches
  `extra`, `m`, still `extra`, `m` after the successor's own proof lands, no third transaction for anybody to lose, and
  no `engine.switch_failed`. Under the rule v0.12.35 withdrew this fails on the sequence, because that caller coalesced
  onto the successor's switch and then claimed again. In `SwitchFairnessTests` the two `Task.Delay(150)` waits that let
  a newcomer "arrive" are replaced by a count on `BeforeSwitchWaitForTests`, the seam that runs on the way to the
  switch-lock wait: two arrivals is the fact the class needs — the newcomer reached its own decision point, and with the
  first switch held by its launch gate the only thing left to it is that switch's wait. That class's `WaitUntil` also
  returned quietly when its deadline passed, which made every ordering assertion after such a wait a check of whatever
  the scheduler happened to do; a missed wait now fails as a timeout. No behaviour change: `src/` is untouched, so the
  AOT publish measured at v0.12.35 covers this source.
- v0.12.35 — fix-forward over v0.12.34: a waiter that holds the proof **its own switch published** keeps that answer
  (§5.5, decision-36). v0.12.34 left `main` red, and the defect was its rule rather than its test: `ReadyIn` answers
  false while `SwitchingToward` is set, so the marker a **later** claim raises withdrew a proof this router had just
  published, and the waiter that held it fell through to claim again. CI on that commit ran 191/192 with
  `SwitchFairnessTests.AWaiterBehindAnotherProfilesSwitchTakesItsOwnTurn` failing at that caller's own 20 s ceiling —
  it was waiting for the successor's switch, which the test had its launch gate on — and that commit's own new pin
  was reproduced failing, showing the launch sequence `extra, m, extra, m`: two callers wanting different models on a
  one-model engine alternating lifecycle transactions, the thrashing shape decision-25 rules out. What changed: the
  resume answers are four, in order — the snapshot authorises the target, so Ready; the target is still stood behind
  by this caller's own published proof (in the reported list, a fitting window, health absent **only** because a
  marker is up), so Ready; this caller's own attempt ended false, so Failed once; anything else — a report that no
  longer names the target, a window that no longer fits, health withdrawn with no switch in flight (a probe), or a
  switch aimed at another target or profile — takes its own turn through the claim path. The Failed-once rule, the
  reason categories and the caller-facing wording from v0.12.33/v0.12.34 are untouched, and no wire surface changes.
  Pinned by `SwitchClaimTests.AWaiterHoldingItsOwnProofIsNotQueuedBehindAnotherCallersClaim` — the successor claims
  the engine while the first waiter is held in the seam, its launch stays on its gate until after the answer, and the
  waiter comes back Ready off its own proof with launches exactly `extra, m` and its successor's switch completed
  rather than rolled back — and by `AWaiterOvertakenAtTheResumeReadTakesItsOwnTurnInsteadOfAnAnswerItCannotSupport`
  re-armed on the same seam: the contradiction is now the engine's own report, the successor's switch running to
  completion and its proof dropping `extra` from the reported list *before* the waiter reads the snapshot, so the
  waiter takes exactly one turn of its own (`extra, m, extra`) rather than an argument it cannot support. v0.12.34's
  pin of that name asserted the ping-pong instead of the rule — its successor had only *claimed*, and one assertion
  turned on which waiter woke first. Mutation evidence on this tree: restoring the v0.12.34 fall-through fails the
  new pin at its own ceiling and fails the fairness pin, and moves nothing else; dropping the contradiction check, so
  a proved switch always answers Ready, fails the re-armed pin. **Not closed here:** a waiter answered Ready off its
  own proof keeps an answer that can outlive the model, because the request is enqueued after the wait returns with no
  revalidation before the engine receives it. That is unload authority — the switch's own reservation ticket
  (TASK-25) and the age-weighted drain that decides whose switch runs (TASK-72) — and is recorded in §15.
- v0.12.34 — a waiter is authorised **when it resumes**, not when it was promised (§5.5), the resume half of the
  publication-honesty fix set (TASK-61), whose shape a top-tier plan review corrected. What changed: at resume the
  three answers are now ordered — the current snapshot authorises the target, so Ready; the caller's own attempt ended
  false, so Failed once, because nothing retries inside the engine and a caller with no ceiling would otherwise
  relaunch a switch that cannot improve; an attempt that *succeeded* but whose snapshot no longer authorises the
  target takes its own turn through the claim path rather than being refused. That third case was the defect: the end
  of a switch clears the marker and settles the readiness task before the waiter is scheduled, so a successor can
  claim the engine in that window, and the overtaken waiter was being told `switch to X failed` for a switch that had
  just succeeded — a wire answer contradicting the feed, and a request killed for someone else's claim. Refusing it was
  the plan-review's rejected shape: single-flight is per engine, not per target (decision-9), so an overtaken caller
  queues behind a new attempt of its own, bounded by its own ceiling. No wire surface changes; the categories
  introduced in v0.12.33 are untouched. Pinned by
  `SwitchClaimTests.AWaiterOvertakenAtTheResumeReadTakesItsOwnTurnInsteadOfAnAnswerItCannotSupport`, which parks the
  first waiter in the existing seam **between the readiness await and the snapshot read**, lets a second claim win the
  engine, releases it, and asserts the waiter is neither told Ready nor refused — three launches (`extra`, `m`,
  `extra`), no `engine.switch_failed`, and Ready only off its own attempt; the whole pin runs in ~0.1 s in Release, so
  it cannot be passing on thread-scheduling luck. Mutation evidence: dropping the `!ready` guard fails that pin and
  nothing else. **Not closed here:** holding that authority across the admission handoff is TASK-25's criterion, and
  the scheduler-pass/drain handoff stays an accepted gap (TASK-64, §15). **Superseded in v0.12.35:** the third case as
  stated here — any snapshot that no longer authorises the target sends the caller back through the claim path — is
  what made this revision red; a later claim's marker is not a contradiction, and the rule is restated above.
- v0.12.33 — a switch the engine cannot confirm is **refused**, not served (§5.5, §6.1): the owner's answer to the
  posture question (decision-27), which is the half of TASK-62 that was policy rather than a bug. What changed: on the
  `wait_health = false` path a `/models` refresh that **could not answer** ended the switch ready — the exec's exit
  status was served as if it were an observation — while v0.12.30 had already refused a refresh that answered without
  naming the model. Both now refuse the same way: `engine.switch_failed` with a reason naming what could not be
  confirmed (`refresh_unanswered_<model>` is the new half), then unknown, then not-ready, so the router vouches for no
  model on evidence nobody made. The caller-facing half moves in the same commit: a `switch_failed` body now names the
  reason **category** its own switch ended with (`refresh_omits_…`, `refresh_unanswered_…`, `health_timeout …`,
  `launch_failed`, `window_…_lt_…`, `no_select_command`, `switch_error`), recorded on the switch state before its
  readiness task settles so a waiter cannot read another attempt's reason, while the operator's sentence — including
  anything a lifecycle provider said, now bounded where it is written — stays on the feed where a non-admin caller
  cannot pick up a socket path or a selector's stderr. No new wire surface: the refusal travels the 503 `switch_failed`
  path a caller already sees. The refusal reason's variable half is bounded in **length** as well as in name count —
  eight names at most, each cut with its true length marked, and the count of what was left out always stated — because
  the event ring caps entries rather than characters and an engine chooses its own model names. Tests: the pin that
  pinned the lenient answer is rewritten (`ASwitchTheEngineCannotConfirmAfterTheLaunchIsRefusedAndClaimsNothing`, was
  `ASwitchTrustingTheExecThatCannotAskWhatIsLoadedClaimsNothing`); one new bounded-reason case
  (`ARefusalReasonStaysBoundedWhenTheEngineReportsManyLongNames`: thirty names of ninety characters, where the counts
  survive and the strings do not); and the first request-side walk of a refusal
  (`A_Reported_Near_Miss_Refuses_The_Request_And_Dispatches_Nothing`): an aliased request is refused with the canonical
  target named, the fake engine's new completion counter reads zero so nothing was dispatched, the feed names the near
  miss the engine did report, and the capability reads unknown. Three earlier trust-path pins needed an engine that
  answers once refusal is the rule — `AClaimedSwitchIsInvisibleToDispatchAndOutlivesAStaleEndOfTheLastOne` and both
  `SwitchFairnessTests` now report the model, and the profile pair reports a real window — because a switch that cannot
  be confirmed no longer ends ready, and those pins are about the marker and the profile, not about the refusal. §15
  also records the accepted gap TASK-67 AC #5 leaves: the raced second launch is closed by the claim's version
  compare-exchange on the snapshot its readiness answer came from, and is not pinned because a seam inside the claim's
  await-free region costs more production surface than the race is worth. **Not closed here:** whether a *stale* window
  answer may authorise a profile is TASK-68; the known flake in the nudge pin is TASK-80; and a caller whose own switch
  succeeded but whose snapshot no longer authorises its target is TASK-61.
- v0.12.32 — a switch the router stopped is answered as **cancellation**, not as a failed model load (§5.5), from the
  shutdown pin in the v0.12.31 set failing in Release. What changed: shutdown **settles the in-flight switch's waiters
  before it cancels** the switch token, so a waiter parked on a readiness task is not released two ways with two
  meanings (the marker and switch state stay for the transaction's own end, since a load is still running); `EndSwitch`
  records on the switch state, before settling the readiness task, whether the engine's switch token was already
  cancelled; and a wake-up that came from the token rather than from the switch is folded into the same decision, so
  the waiter's answer is the same on both routes — `OperationCanceledException`, which the request path reports as
  `cancelled`. The v0.12.31 hunk released the two waits by cancellation but left the in-flight waiter reachable by the
  other route: the provider sees the engine's own token, returns `cancelled_during_launch`, the switch settles false,
  and the caller was told its own model load had failed — a shutting-down router described as a broken engine, the
  failure shape that hunk exists to remove. Found while pinning that: `EngineLifecycleCoordinator.Dispose()` disposed
  its lifecycle gate under a transaction still holding it, so the release in that transaction's own `finally` answered
  `ObjectDisposedException` and the switch published it as its failure reason
  (`engine.switch_failed: Cannot access a disposed object. Object name: 'System.Threading.SemaphoreSlim'.`); the gate
  is now never disposed, for the same reason the switch lock is not. A launch that fails with the token untouched is
  unchanged — still `switch_failed`, still with the exec's reason on the feed. **Why CI caught it and the earlier
  report did not:** the pin was green in Debug, and scheduling-dependent in Release — CI passed it at `9c8c57d` and
  failed it at `c4a8828` and `6863b9a` (runs 44221179468, 44221520169: Failed 1 / Passed 186 / Total 187, at
  `SwitchClaimTests.cs:183`), and 4 of 5 focused Release runs failed here at `970c25b`. Measurement in Release at this
  change, each hunk reverted: answer-guard removed → the new pin fails 6 of 6; lifecycle gate disposed again → 4 of 4
  fail at class scope (the disposed-object reason replaces the provider's cancellation on the feed) and 8 of 8 pass in
  isolation, which is why the pin runs with its class; shutdown settle-before-cancel removed → 8 of 8 still pass,
  because the settle route already dominated here, so that ordering is recorded as construction rather than as a
  mutation-caught regression and the new seam records which route each waiter woke on, making a flip visible instead
  of silent. New: seam `AfterReadyWaitForTests(bool wokeByCancellation)`, `AWaiterInsideTheReadinessWaitIsToldTheRouterStopped`,
  `ALaunchThatFailsOnItsOwnIsStillToldAsAFailedSwitch` (a real failure is still `switch_failed` with its reason on the
  feed), and the shutdown pin now also waits for the provider's `cancelled_during_launch` event so it cannot pass on a
  switch that never reached the exec. Suite at this commit in Release: 189 passed / 189.
- v0.12.31 — the switch claim made **atomic**, and its end ordering and failure edges pinned (§5.5), from the
  plan review that split the set-1 fix. What changed: the readiness decision and the switch marker now come from one
  capability snapshot — `PublishSwitchStarted` became `TryPublishSwitchStarted(model, expectedVersion)`, a
  compare-exchange on the version the decision read, and a lost exchange re-decides on the newer snapshot instead of
  launching over a proof a poll had just published (the claim loop reads `ReadyIn(cap, …)` against that same
  snapshot, so a poll that proves the caller's model retires the claim outright). `EndSwitch` settles the readiness
  task **before** the dispatcher nudge, and the nudge runs with the transition gate released and its own throw
  swallowed: previously the hint ran first and unguarded, so a throwing callback escaped into the switch's catch and
  rewrote a switch that had already succeeded — failed event, health withdrawn, waiter told Failed — and parked the
  waiter until that catch ran. A synchronous failure at the scheduling boundary and a claim hook that throws both
  undo their claim, the hook's undo taken after leaving the reentrant gate rather than inside it. `_switchLock` is no
  longer disposed on shutdown, and both waits carry a token linked to the engine's switch token, so a shutdown
  during a claim arrives as cancellation (`cancelled` on the wire) rather than `ObjectDisposedException`, which the
  request path could only report as `engine_unreachable`. Test seams: `BeforeSwitchWaitForTests`,
  `BeforeScheduleSwitchForTests`, and `AfterEndCommitForTests(bool insideGate)` — the flag is what pins the rollback
  and nudge ordering rather than asserting about it; `AfterClaimForTests` is now wrapped so a hook's throw is rolled
  back. Five new tests: four in `SwitchClaimTests` (throwing nudge, scheduling failure, shutdown-during-a-claim,
  hook rollback, plus the unchanged-path control that an unsuperseded switch still returns Ready and nudges once
  outside the gate) and one in `CapabilityPublicationTests` (`ASecondCallerTakesTheProofInsteadOfClaimingASecondSwitch`,
  which parks the newcomer and releases it only after the switch in front has settled, so the claim is decided
  against an idle state and a proven snapshot with no dependence on thread scheduling). Mutation evidence, each hunk
  reverted: nudge restored before the settle → the nudge test fails, the control stays green; claim skips the
  re-decision → the newcomer test fails (second switch); `_switchLock.Dispose()` restored → shutdown reports
  `ObjectDisposedException` where cancellation is expected; rollback moved inside the gate → the recorded gate flag
  flips to true. **Not closed here:** the window and profile gauges still carry no epoch (TASK-63 AC #2), and the
  resume-authorisation half of the same review — a waiting caller whose snapshot no longer authorises its target —
  is its own row (TASK-61).
- v0.12.30 — a switch that trusts its exec still has to be told the engine serves the model (§5.5), from the
  same plan review of the publication-honesty fix set. What changed: on the `wait_health = false` path a post-launch
  `/models` refresh that **answers** was published as a proof and the switch ended ready whether or not the reported
  list named the requested model — the router could vouch for a model nothing had ever reported, with the switch's own
  say-so as the only evidence. The membership check the prove-health path already made is made here too, and made
  **before** the proof publishes, so a refused switch leaves no positive snapshot behind and burns no version on an
  engine it is about to refuse: the exit is the existing `engine.switch_failed` kind with reason `refresh_omits_<model>`,
  naming what the engine did report, then unknown, then not-ready. No new wire surface — the refusal travels the
  `switch_failed` path a caller already sees (`RouterCore`'s switch-wait failure branch). §5.5 also states the namespace
  that rule assumes: an engine's reported names and the catalog's model keys are one namespace matched case-insensitively,
  and a catalog **alias** never satisfies the check, so a report that differs only by alias or provider-side spelling is
  refused and the fix is config rather than a router-side translation. Three new `CapabilityPublicationTests`: the
  omission refuses with an actionable reason and keeps the older list as history only; a refresh that names the target is
  a **positive control** for the trusting path (ready, verified, one publication); and the match folds case but not
  aliases. **Not closed here:** a refresh that cannot answer at all still serves the request on the exec's word — that
  posture is an open owner decision (TASK-66) and the test pinning it moves with that answer, not with this commit; the
  wire wording behind a refusal on the other posture is TASK-62 AC #2, which waits on it.
- v0.12.29 — the probe fence made **two-directional** (§5.5), from the plan review of the publication-honesty fix set.
  What changed: a health-loop failure publishes its negative under the same version fence the successful answer uses —
  a new `PublishProbeFailure(expectedVersion)` holding the negative half that `PublishProbeResult` already carried — so
  a poll that could not reach the engine *before* a switch proved that engine no longer marks it unknown for a whole
  interval, which costs that model its engine (the scheduler skips an unhealthy engine) and takes the admission
  refusal's grounds away with it; the version for that fence is read outside the `try`, because a catch that cannot see
  it cannot fence on it; `PublishUnknown()` is documented as **unfenced on purpose** at every switch exit — a switch is
  the fresher publisher by construction, so fencing it would let a quiet poll drop the publication that says the engine
  was just stopped or relaunched; and §5.5 now states which observation moves health at all (the `/models` fetch) and
  that the serving-state probe's `health.expect` verdict moves none, which the code comment claimed and nothing pinned.
  The quiet-poll wording says what the comparison actually does — length-equal then index-by-index `OrdinalIgnoreCase`
  against the list currently published — rather than naming the array the code reuses. Three new
  `CapabilityPublicationTests`: a straddling failed poll cannot withdraw a switch's proof (needed a `ProbeHandler`
  seam able to fail a *held* request, since one status code applied before the hold meant a held probe could only ever
  answer 200); a poll that cannot ask what is loaded still marks the engine unknown when nothing newer has landed; and a
  failing `health.expect` rule moves no health flag while the gauges still move. **Not closed here:** the gauge updates
  still carry no epoch, so a stale window answer can still mislead readiness and the escalation ladder (TASK-63 AC #2);
  and the fence still orders publications rather than engines, so a restart the router did not see evades it (§15).
- v0.12.28 — an engine's account of itself is either **current-and-verified or unknown** (§5.5), from two plan
  reviews of the change plus a live ordering bug found while checking the first round's correction against the code:
  a switch's start installed the runtime's private switch state *before* publishing the snapshot marker, so a
  dispatch pass in that window saw an engine whose switch had already been claimed as idle and sent a request into
  an engine that was about to be stopped. What changed: claiming a switch publishes the marker and `Healthy = false`
  in one publication **before** the switch state is installed; every probe re-checks the snapshot version inside
  each publish retry, so a fetch that began before a switch cannot land afterwards and replace what the switch
  proved; every switch exit that did not verify the engine — failed stop, failed launch, readiness timeout, an exec
  the router cannot check (`wait_health = false`) — publishes `Healthy = false` as a health-only change that keeps
  the currently reported list, so a stale list can neither refuse a model that has begun to be served nor vouch for
  one that was just stopped; a successful readiness proof publishes the reported list and `Healthy = true` together,
  which makes the engine eligible without waiting for another poll; a probe whose payload matches what is already
  published leaves the version alone, so a healthy engine cannot spin the refusal scan's settle-loop; `IsReadyFor`
  answers from a healthy, marker-free snapshot holding the target with a fitting window rather than from the
  reported list and the window gauge alone; and both ends of a switch are ordered by one await-free gate — the claim
  publishes marker-then-state, the end writes idle state and clears the marker only while the marker still names
  that switch, and the switch-state read used for the claim/coalesce decision happens inside the same gate — with the
  commit signalling the dispatcher directly, because the outcome events fire *before* the commit and a pass woken by
  them sees the marker still up and skips the engine until the next health poll. Ten new
  `CapabilityPublicationTests` pin the marker-before-claim order, the fenced stale poll, unknown-at-uncertain-exit,
  proof-restores-health without another poll, quiet-poll version stability, the nudge arriving only after the commit,
  and that a claimed switch is invisible to dispatch and outlives a stale end of the last one. **Not closed here**:
  the fence orders publications, not the engine, so a restart the router did not see evades it (§5.5, §15); the
  snapshot still hands out a mutable array, which is its own task; and the quiet-poll comparison is content-wise, so
  order-only and duplicate-count churn still bump.
- v0.12.27 — the capability refusal made **coherent**, from the commit review of the code that shipped it and the
  plan review of the fix. What changed: what one engine is known to serve is now **one immutable, versioned
  snapshot** (`EngineCapability`: health, reported models, switch marker), written only by CAS-publish at each of
  the five places those values change (health probe success and failure, switch start, switch success/failure,
  post-switch refresh), so a version can never move independently of its data and an unchanged poll is not a
  publication; the refusal is decided by `CapabilityScan.DefinitelyHopeless` over those snapshots, requires the
  version sum to be unchanged across the pass, retries once, and answers "not hopeless" when it will not settle;
  the scheduler's view derives loaded list, switch marker and eligibility bit from **one** snapshot read per
  engine instead of two reads of a volatile array; and the refusal is evaluated **before** the queue cap, so a
  hopeless model is answered 503 `model_unavailable` rather than 429, and on the feed it is one `queue.rejected`
  with no `request.enqueued` (the ticket never entered a queue). The switch marker lives inside the snapshot
  because a marker cleared before the model appears in the reported list is the same torn state in another
  place. New `CapabilityScanTests` pin the rule deterministically: a publication mid-scan voids the answer, a
  scan that never settles never refuses, a switch toward the model is not an absence, an unprobed engine is not
  an absence, a reported model outside any catalog is servable, and no engines is never hopeless. **Not closed
  here**: a request admitted while capability was unknown is not reclassified once absence becomes known (§5.5
  now states this as a limit), and `_queues` keeps one entry per distinct model name with no global depth bound.
- v0.12.26 — documentation only: what the release controls actually lock, measured rather than assumed, from
  the disposable-repository battery (TASK-49) and the first live tag. §14's published-surface bullet now says
  what has run (one tag, four jobs green, five assets `uploaded`, served-byte digests equal to the records) and
  what the two controls do: **the tag ruleset (`update` + `deletion`, `bypass_actors: []`) is the only measured
  protection of the tag ref**, and it binds the Actions token as well as an admin token; a **`creation` rule
  would break this pipeline** (it refused the Actions token's brand-new tag push, an admin push, and the release
  API's implicit ref creation), so the shipped ruleset omits it; **immutable releases burn the version name**
  (the name stays reserved after the release is deleted) **and lock nothing else measured** — assets were deleted
  and a different file re-uploaded under the same name and the download serves the new bytes, release and asset
  metadata answered `PATCH 200`, the release itself answered `DELETE 204`, and with no ruleset a force-push moved
  a published immutable release's tag. Asset integrity therefore rests on the run's digest records and the
  post-publish readback, which makes the readback load-bearing rather than optional. **One earlier claim
  corrected in place**: this SPEC said GitHub allows two releases on one tag; it does not — a second published
  release on the same tag answers `422 {"code":"already_exists","field":"tag_name"}`. The fail-closed list read
  is kept for the reason that survives measurement: that same read omitted a release created moments earlier,
  three times.
- v0.12.25 — a request for a model nothing here can serve is refused **at admission** (§7). Before: the
  scheduler answered `Wait` and the request kept its place — and its head position — in that model's queue
  until `max_queue_time` expired (408 `deadline_exceeded`), or **forever**, because with no `max_queue_time`
  no timer is armed and the dispatch-time check needs the option. Now `AnyEngineCanServe` (every engine has
  answered its model probe, and none declares the model in its catalog or reports it) fails the ticket with
  the already-declared 503 `model_unavailable`, after its `request.enqueued` event. Two points from the plan
  review are load-bearing in the shape, both reproduced against this code first: eligibility is the catalog
  **or** the models the engine reports live (`/models`), so a model loaded out-of-band still serves — a
  catalog-only refusal would have broken it; and the check runs at admission, not only at dispatch, because
  dispatch examines each model queue's head and a hopeless ticket behind a stuck head would still hang. One
  predicate, `EngineRuntime.CanServeModel`, shared with the scheduler so the two cannot drift. Unknown
  capability is never hopeless (an engine that has never answered `/models` says nothing either way), and
  zero engines is refused by config validation, so the scan is never vacuous. Wire: §6.1 drops
  `no_available_engine` 500, a code nothing ever emitted (it existed as a scheduler diagnostic and in the
  status map only), and states that wait reasons are diagnostics rather than contract. `MaybeFailDeadline`
  loses a `reason` parameter it discarded.
- v0.12.24 — documentation only, from the first live run and the ruleset act. **Fixed**: §7 claimed the
  per-model queue overflow is `408`-mapped, while §6.1 and `ServerApp.MapStatus` say `queue_overflow` →
  **429**; the two 408s had been conflated (overflow and the queue deadline). In checking it: the overflow
  *code* is pinned at library level and its body shape at wire level, but **no test asserts the 429** — said
  here rather than quietly asserted. **Recorded** as an accepted gap in §15: with the tag ruleset carrying
  `update` + `deletion` and no `creation` rule, a release tag can be created before it is protected, so the
  window between "name exists" and "name is frozen" stays open by design. Nothing here is assumed: the two
  refusals, the successful creation of a new `v*` name, and the gate's own tag-equality refusal all come from
  measured runs.
- v0.12.23 — the gate ran for real for the first time, and the fourteenth deep review of it. **Fixed, from
  the live run** (tag `v0.0.9-canary`, refused before publishing): each leg's checksum record was compared
  as raw bytes against a text-mode computation, and Windows coreutils writes its record in binary mode
  (`<digest> *<name>`) where GNU coreutils on Linux writes `<digest>  <name>`. The digests were equal and
  the message claimed a checksum mismatch. Every leg now writes its record with `sha256sum -b`, and the
  verification compares the digest field and the name field separately, naming which half disagrees, with
  an empty or non-printable record refused on its own terms. The harness wrote only the Linux form, which
  is why its 122 cases were green while the real leg was refused; it now pins both record modes, a mixed
  pair, and a record whose digest is right and whose name is another archive. **Fixed, from the review**:
  `rsum_line=340` was not a bound — numbers and booleans bypassed every cap, so a long numeric field could
  print in full and push the flags out of the summary — and the harness case that claimed to test the bound
  tested a 101-character value against it. Every value is now sanitized and capped to its own cap whatever
  its JSON type, the bound is 720 with its arithmetic written next to it, the cut marker sits outside the
  quoted fragments, and a 40-digit id case proves a number cannot print in full. `http_codes()` rescanned
  its own match for digit runs, so `HTTP/1.500 200` reported 500 and 200 and `status:1400x` reported a
  code; it now takes the status digits only, and a read that fails says `unreadable` instead of the same
  thing as "no such field". `fdiff()` reported a first difference at length+1 for two equal inputs and
  cost seconds on long ones; it now says `values are equal` and compares at most 4096 characters. One
  remaining `echo` of a response-derived value became `printf '%s\n'`. Harness 122 → 132 cases.
  **Measured on the repository**: a ruleset created without a `conditions` object reads back as
  `enforcement: active` and protects nothing — probe pushes moved and deleted the canary's tag while that
  ruleset stood. With `conditions.ref_name.include: ["refs/tags/v*"]` the same probes are refused
  (`GH013 … Cannot update this protected ref.` and `Cannot delete this tag`), and cutting a new `v*` tag
  is still allowed because no `creation` rule is set. The ruleset update verb is `PUT`; `PATCH` is a 404.
- v0.12.22 — a thirteenth deep review of the gate, over the v0.12.21 gate. Both of its P1s were reproduced
  locally before anything was adopted, and the first one was a defect in my own previous fix. **Fixed**:
  `brief()` cut the **whole summary line** at 200 characters head-only after the per-field caps had already
  built it, so a long tag or name erased the `upload_url` at the end of the line — the "keep the tail"
  behaviour bought in v0.12.21 was being undone one step later. The bound became an argument, the cut
  kept head and tail with the true length, and escaping ran after the cut so `%25` and `%3A` stayed intact.
  This revision used 340 for the summary limit. The next review found that numeric fields could bypass
  the per-field caps. The original `line_bound_tail` fixture produced 176 bytes before the old cut and
  passed on both versions. The v0.12.62 harness correction gives both the tag and title 41 characters,
  producing 216 bytes on the old step. It pins all six labels, the exact field markers, and the missing
  URL tail on `79a21e0`. The current step must retain the tail. A 198-byte historical control must also
  retain it.
  `rsum()` now renders presence and type visibly: `<missing>` for a field the response does not carry,
  `<null>` for a null it does, `<object>` / `<array>` for containers, and a JSON-quoted string whose head and
  tail are quoted separately so the `+(Nch)+` marker sits outside the quotes. Before this, `{"draft":"false"}`
  printed exactly what `draft=false` printed, and `{}` printed exactly what `{"id":"absent"}` printed
  (`type_presence`, three responses). Status extraction did not match `HTTP/2 500` at all — the version `2`
  sat in the `[^0-9]{0,4}` gap — while matching `mystatus=403`; it now matches an HTTP version plus a status,
  or a status-shaped label, and refuses `HTTP/2 5000`, `500ms` and `4040` (`http2_status`, both directions).
  The six refusals that quote a response now use `printf '%s\n'` rather than `echo`, because `xpg_echo` would
  turn a literal `\n` into a newline *after* sanitizing and could start a second log line. And a refusal about
  the upload address names **where** it differs from the documented address (`fdiff_offset`: first difference
  at character 71), because two 124-character addresses differing only in the middle render identically under
  head-plus-tail truncation, so printing both is not a comparison.
  **Accepted as limits, not bugs**: a value can spell a truncation marker (the quoting now makes it visibly
  inside the value); a NUL in a response is dropped by command substitution, which no shell-side fix reaches;
  a `jq` parse failure at the depth limit still fails closed into the refusal; `tr` expands each non-ASCII
  byte to one `?`, bounded by the outer cut. Harness 115 → 122 cases, 0 failures; it runs as a CI job of its
  own (`tools/relgate.py`), and it still proves control flow and response reading only — no tag has run the
  gate, and the `GET`-to-`PATCH` race, the canary (TASK-42), the repository controls (TASK-38/45) and readback
  of **served** bytes (TASK-48) remain open.
- v0.12.21 — a twelfth deep review of the gate, over the v0.12.20 gate: it agreed that removing the stderr
  channel (rather than filtering it) is the right shape, and showed that the per-field caps added in v0.12.20
  did not bound every field. **Fixed**: a field that arrives as an object or array is now named as a container
  (`<object>` / `<array>`) instead of printed with `tojson` and no cap at all, which is the mistake the review
  predicted — a differential pair proves both halves (`url_object_prev` walks a 400-character string out of a
  nested value up to the outer cut; `url_object_field` prints `<object>` and no content). A truncated string
  now shows its **head and tail** with the true length between them: the outer 200-character join cut what a
  long `upload_url` had of interest, because the part that distinguishes one release's address sits at the end
  (`long_url_prev` vs `diag_fields_survive`). The summary also stops summarising rubbish — `jq -s` plus a
  `length != 1`/type guard means one response gets one summary, and an empty or multi-document answer says
  `unreadable response` instead of printing blanks or several lines. The status extraction runs under `LC_ALL=C`
  (the character ranges were locale-dependent) and is **relabelled** to "status-like numbers mentioned in
  stderr": a response can write `mystatus=403` and forge a status, so the honest claim is about the text, not
  about HTTP. The forged-marker observation is pinned rather than fixed — a value that already spells
  `+(999ch)` passes through as text (`name_forged_cap`), because the count is ours but the shape is not a proof
  about the value. **Counting corrected**: the step's discard sites are nine `2>/dev/null`, six stdout-only
  shape checks and one `>/dev/null 2>&1` — sixteen, not the eleven I reported (the board's nine was right for
  `2>/dev/null` alone). **Verified, not assumed**: those six stdout-only sites are not a leak channel — measured
  with jq 1.8.1, jq names the offending **value** only for typed operations (`floor`, `test`, arithmetic) and
  truncates it to ten characters, while every typed operation in these programs sits behind a type guard, so the
  errors that can fire name the index and never the value (`shape_jq_stderr`; the reasoning is a comment in the
  step). **Limits stated**: jq accepts `NaN`/`Infinity` and lets a duplicate key win by last occurrence; the
  caps slice by codepoint while `brief()` works on bytes, and a grapheme cluster can split at a cap boundary —
  cosmetic, since `tr -c` already turns every non-ASCII byte into `?`; a value can still make the summary look
  truncated; and `dotnet publish … 2>&1 | tee` stays unfiltered on purpose, because compiler output is the
  point of that log. **Harness as of this revision**: 115 cases, 0 failures, exit 0; no .NET code changed, so no
  build or test run. No tag has run the gate: still configured, not proven.
- v0.12.20 — an eleventh deep review of the gate, over v0.12.19: it held that constructing the upload
  address is real defense in depth and that the two things I had just added to protect the **log** did not do
  what their comments claimed. **Fixed**: the release-list read no longer filters its stderr, it removes the
  channel — the annotation carries the exit code and any three-digit HTTP status extracted from the text, and
  nothing else, because redaction by pattern is unreliable (a short token, a Basic auth header, an
  `x-access-token` clone URL, base64 interrupted by `+` or `/`, or a credential split by the byte cap all walk
  past a regex), and a digits-only answer cannot carry any of them (`pre_errtoken`); the now-dead copy of the
  sanitizer in that step is gone. The create-refusal summary is bounded **per field** — id, tag, name, draft,
  prerelease and the address each with its own cap and its true length — so a 300-character title can no longer
  push the flags past the 200-character cut (`name_crowding`), and a field of the wrong type is shown as JSON
  rather than collapsing the line (`url_wrong_type`); the earlier version also printed `tojson` of the whole
  response instead of the field, which the new cases caught. The cleanup step's summary of a release lookup is
  bounded the same way, so the two jobs describe a bad response in the same shape. **Verified rather than changed**: the address
  literal in the file is a plain string (`cat -A` shows `upload_base="https://uploads.github.com/…"` — the
  review's Markdown-link worry was an artifact of how my own payload was rendered), and asset names reach the
  `?name=` query only through an allowlist of literal leg ids, so a character needing percent-encoding cannot
  appear in one. **Limits stated rather than implied**: the built address equals the validated one up to
  owner/repo **casing** — the compare folds those components while the built address uses the job-context
  spelling — which fails loudly if GitHub ever makes those path components case-sensitive, and a repository
  rename mid-run would leave the old name; the diagnostic is a sanitizer, not a redactor, so what a response
  chooses to put in its own `name` is still shown, control-free; and the temp-file `EXIT` trap is best effort
  (it cannot cover `SIGKILL` or a runner crash, and a failing `mktemp` aborts before the trap exists, which
  still fails closed). **Not closed**: the read-to-`PATCH` race and the cleanup `GET`-then-`DELETE` window,
  which no YAML change can close — the controls are repository settings, and the plan to apply them was
  reviewed separately (below). **A claim this SPEC repeated is corrected here**: that "GitHub permits two
  releases to carry one tag". It is documented nowhere in the API description this repo holds, and the
  reviews have asserted both directions without testing either; the preflight refusal does not depend on
  the answer, because it also blocks a half-finished draft and a build that create would refuse anyway, and
  the canary cannot settle it (one release attempt per tag). A throwaway repository can. **Harness as of this revision**: 110 cases, 0 failures. No tag has run the gate: still
  configured, not proven.
- v0.12.19 — a tenth deep review of the gate, over v0.12.18: it judged the preflight repair and the
  address compare substantially right and pushed on what they cannot prove, so this revision is mostly about
  what the run **routes** and what it **logs**. **Fixed**: the upload target is built here — repository
  context plus the validated digits-only id — instead of sliced out of the server's `upload_url`, which is
  still required to equal exactly that address as a contract check, so a response that spells owner/repo
  differently cannot reroute a request (`built_upload_target`: 5 uploads at this step's own address, 0 at the
  answer's spelling); the captured stderr of the release-list read is capped at the first line of 2 KiB and
  credential-shaped runs are replaced *before* sanitizing, and the temp file is removed by an `EXIT` trap
  rather than two `rm` calls (`pre_errtoken`: a 40-character token on stderr does not reach the log); the
  create-refusal diagnostic is now one field at a time with the address bounded to 60 characters plus its
  length, so a long value cannot push the id, title, draft and prerelease past the 200-character cut
  (`diag_fields_survive`); the bounded address is also type-safe, because a create answer whose
  `upload_url` arrives as a number made the diagnostic collapse to "unreadable response" and lose the fields
  that matched — it now prints the value as JSON (`url_wrong_type`). **One review claim checked and rejected**: that folding the owner/repo prefix
  admits spellings the previous revision refused — the compare it replaced folded the same prefix
  (`${got_prefix,,} != ${want_prefix,,}`), and folding over scheme, host and owner/repo only, exact from the
  release id on, has been the deliberate rule since v0.12.15. **Limits stated rather than implied**: the
  shape check proves the read parsed, not that it was complete — a `200` whose array is shorter than reality
  is undetectable by exit status or shape, and a body that repeats a key (`tag_name` twice) makes jq keep the
  last value, so the tag match can be defeated by JSON GitHub would not send unless something else reports
  duplicate keys; the integer guard validates jq's printed representation, not the wire spelling (`1e0` prints
  as `1` and is accepted, which is the right rule for a step that interpolates the value), and the `< 2^53`
  ceiling rejects a future legitimate larger id on purpose; a step killed before its trap still leaves the
  temp file. **Cheaper design offered and not taken**: a direct tag lookup instead of listing releases — that
  endpoint answers published releases only, and hiding a half-finished draft is the whole reason the listing
  exists. **Not closed, and unchanged by this commit**: what another release-write principal does between the
  last read and the un-draft `PATCH`, and cleanup's `GET`-then-`DELETE` window; rulesets (TASK-38), a
  restricted set of publishers and immutable releases (TASK-45, off) stay the controls. **Harness as of this
  revision**: 109 cases, 0 failures. No tag has run the gate: still configured, not proven.
- v0.12.18 — a ninth deep review of the gate, over v0.12.17: it attacked how the release list is read and what
  a projected field's equality actually means, and one of its inferences reproduces as a real publication.
  **Fixed, each with cases in the harness**: (1) the preflight release-list read still failed open. `gh api
  --paginate … || true` threw the exit status away, and measured on gh 2.46.0 a page that fails mid-walk still
  prints the pages it fetched before exiting 1 — so the variable is non-empty, the "could not read" guard does
  not fire, and a release sitting on a page that was never fetched reads as *no release*: the build then creates
  a second release on a tag that already carries one (`pre_partial_page` runs 05f224b and shows it going
  through). The exit status is now the authority, and the JSON's **shape** — one or more documents, each an
  array of objects carrying a string `tag_name` — is checked **before** the tag is selected, so an error body is
  a refusal and not an absence (`pre_errdoc_body`). A junk array element was already refused by 05f224b, but
  because jq errored indexing a string rather than because anything checked the shape; `pre_junk_item` keeps the
  case and the new message names which rule fired. (2) **A create response whose `upload_url` ends in a newline
  published.** The address was projected with `jq -r` and compared in the shell, and command substitution strips
  a trailing newline, so a field that was not the expected address compared equal to it — `pub_url_trailing_lf`
  runs 05f224b and it publishes. The address is now compared **inside jq against the original JSON** (prefix
  case-folded, exact from the release id on), which still refuses `/Assets` (`pub_url_path_case`). (3) a release
  id that is a JSON number but not a canonical integer (`1.0e2`, which jq prints as `1.0E+2`) is **not** a
  demonstrated bypass — 05f224b also stopped, but at the read-back, *after five uploads addressed from the
  non-canonical spelling* (`pub_id_exponent_old` counts them). The printed form now has to be plain ASCII digits
  in range, and nothing is posted (`pub_id_exponent`). (4) The existing-release annotation embedded a
  server-chosen id through `jq -r`, so a string id carrying a newline wrote a second `::error::` line into the
  log; `jq -c` keeps it inside one JSON string (`pre_id_annotation`: two workflow commands under 05f224b, one
  now). **Corrected in place**: the comment that `--paginate` answers "one JSON document per page" is wrong — gh
  2.46.0 merges the pages into a single array (measured: `jq -s length` is 1), and the shape rule is written to
  hold under either behaviour. **The reviewer's environment is not this machine** (jq 1.7, bash 5.2, GNU coreutils
  9.7 against jq 1.8.1 and bash 5.3.9), so its numeric-printing claims were re-measured here before being relied
  on. **Not closed by this revision, and not closeable in YAML**: stderr from `gh` and `jq` on calls not already
  routed through the sanitizer still reaches the runner log as written; and the reads and the un-draft `PATCH`
  still do not serialize against another release writer — a writer can publish first, replace assets, or create a
  second release on the tag between the last read and the `PATCH`, and cleanup's `GET`-then-`DELETE` has the same
  window, so tag rulesets (TASK-38), a restricted set of release writers and immutable releases (TASK-45, off)
  stay the real controls. **Harness as of this revision**: 104 cases, 0 failures. No tag has run the gate: still
  configured, not proven.
- v0.12.17 — an eighth deep review of the gate, over v0.12.16: the comparisons were typed and per-field, and
  three ways to satisfy a check with more than one answer were still open, plus one fail-open found while
  re-reading the file for this round. **Fixed, each with cases in the harness**: (1) `jq -e` reports the status
  of its *last* result, so a response that is two JSON documents is judged by the last one — and the release
  read-back, the un-draft `PATCH` answer and the cleanup lookup are read as predicates with no field extracted
  from them. Under v0.12.16 a `GET /releases/{id}` that answered a published release first and a draft second
  passed cleanup's ownership guard and **deleted a published release** (`hole_cleanup_two_docs`), and a read-back
  that answered another tag's published release first let the run reach publication and stop only afterwards
  (`hole_readback_two_docs@fd76021`); every answer the gate trusts is now required to be exactly one document
  (`jq -s … length == 1`). (2) `^…$` is not a whole-string guard in Oniguruma, because `$` also matches before a
  final newline: `state "uploaded\n"` and an asset named `"air-win-x64.tar.gz\n"` were hygiene-clean, and the
  snapshot that reads one physical line per asset was a field boundary short of what it claimed; the three
  patterns are now `\A…\z`. Neither revision published on those two, which is why they are pattern cases and not
  differential pairs. (3) `type == "number"` on ids admits `1.5`, `-3` and `1e999`, and the release id is used to
  build an upload address and a compare target, so ids must be canonical integers below 2^53. (4) The asset
  inventory is one page of 100 required to equal the declared count — a complete inventory only while the page
  is not full — so a declared set of 100 or more is refused before anything is created. (5) The log sanitizer now
  reduces a response's text to printable ASCII *before* the 200-character cut, so control characters, ANSI
  escapes, bidi overrides and split UTF-8 become `?`, and cutting after `tr` rather than `head` means a
  diagnostic cannot come out empty under `pipefail`. (6) `GET /releases?per_page=100` in the preflight was piped
  through `2>/dev/null || true`, so a failed or unparseable read answered "no release exists for this tag" and
  the build went ahead; GitHub permits two releases to carry one tag, so an unreadable list is a refusal now, and
  the harness shows v0.12.16 failing open on it. **[Corrected in v0.12.18: that repair was incomplete — the
  `|| true` still swallowed a page that failed after partial output, and the type filter discarded a non-array
  body, so both shapes stayed open and the sentence "an unreadable list is a refusal" was true only of an
  empty read.]** **Three claims from v0.12.16 corrected here**: "no server-chosen
  text reaches stdout" was false as written — diagnostics carry validated asset names, validated ids, and
  sanitized field text; what holds is that the success record is assembled from values this step verified.
  The `target_commitish` reasoning overreached too: the same endpoint documents the field as *unused when the tag
  already exists*, so for a pre-existing tag the resolved target is GitHub's rather than the default-branch head,
  and the "empty by construction" benefit is an untested hypothesis, not a construction — the parameter stays
  omitted and both directions are canary evidence. And the asset `state` compared exactly is `uploaded`, which is
  documented, while `uploader` is a different object field and a failed upload is documented to leave `starter`;
  digest presence beyond the documentation's example is a first-tag reading, not a local proof. **Harness as of
  this revision**: 90 cases, 0 failures. No tag has run the gate: still configured, not proven.
- v0.12.16 — a seventh deep review of the gate, over v0.12.15: the checks were right and the way they
  read the answers was not. Every response check in the publish and cleanup steps was built by projecting
  the JSON into one string with jq `join(" ")` and splitting it again with `read`, and **a whitespace split
  invents field boundaries the response never had**: a null field leaves two adjacent spaces that IFS
  collapses, and a space inside a string value starts a new column. The review produced the counterexample
  and the harness reproduces it — a create response with `name` `"v0.1.0 true"` and no `draft` field
  flattens to `12345 <url> v0.1.0 v0.1.0 true false` and satisfied the tag, title, draft and prerelease
  comparisons at once; v0.12.15 publishes on it. Three more of the same class are reproduced the same way:
  an asset named `"<wanted> uploaded <size> <digest>"` carries its own state, size and digest into the
  columns the snapshot compares, so four bytes of junk are certified as the staged file while the asset's
  honest metadata sits further along the same line, unread; a `draft` field of the string `"true"` is
  indistinguishable from boolean `true` once joined, so v0.12.15 un-drafted on it; and a cleanup lookup
  answered with `tag_name` `"v0.1.0 true"` and `draft` null read, column by column, as this run's own
  draft — the `DELETE` that follows would have taken a **published** release down. All four now fail the
  gate, each shown as a pair of harness cases over one forged answer: the revision that read it that way
  publishes or deletes, this one refuses. So responses are compared as JSON with typed equality (`jq -e`,
  expected values passed as jq arguments, never interpolated into the filter), the id and the list length
  are required before a field is trusted, and response text becomes log text only escaped and truncated —
  the runner reads stdout for workflow commands, so a field value must not be able to speak as one. The
  asset list is required to be well-formed before any of it is parsed: length equal to the expected count
  (a list cut off by pagination is not an inventory that passes), names drawn only from the characters this
  gate's own asset names use, typed `state`/`size`, and a `digest` that is absent or a sha256. Where the
  review is incomplete rather than wrong it is recorded, not followed: it held that the two whole-string
  read-back comparisons were safe, and against the null-collapse it was testing they are, but not against a
  documented boolean arriving as a string — which is why those comparisons are gone as well. The harness
  stub stopped hand-writing an answer per filter: it builds the JSON the endpoint answers with and runs the
  caller's own `--jq` over it, which is what gh does, so an older revision now gets exactly the projection
  it asked for, joins included. Three things from the same
  disposition list went with it: the case-fold is narrowed to scheme, host and owner/repo, so a path segment
  from the release id on that differs in case is refused rather than accepted; the un-draft `PATCH` is no
  longer answered into `/dev/null`, because its response is a release object that gets the same typed check,
  and its failure text says the release may already be public; and the record of what went public is printed
  from the values this run verified instead of from a response, so no server-chosen text reaches stdout at
  all. One trap the harness caught in the act: `${var:0:$n,,}` — a substring and a case-fold in one
  expansion — is an **arithmetic error** in bash and does not stop a `set -euo pipefail` step, and the six
  `upload_url` cases went green for the wrong reason until the expansion was split in two. That is the
  standing argument for running the harness on YAML edits rather than reading them. None of this closes the
  race: a release writer can still change the draft between the last read and the `PATCH`, and tag
  protection, restricted release writers and (an operator setting, not YAML) immutable releases remain the
  controls. No tag has run the gate: still configured, not proven.
- v0.12.15 — a sixth deep review of the gate, over v0.12.14: the repair held, and the new checks were
  weaker than they looked. The `upload_url` test was a **prefix** match, so `…/assets-extra` or a query
  this run never asked for passed it; the comparison is now **exact against the documented template**
  (`https://uploads.github.com/repos/{owner}/{repo}/releases/{id}/assets{?name,label}`), folded for case
  because the API returns the canonical owner/repo spelling and treats those path components
  case-insensitively, and the upload then goes to the address the API returned rather than the one this
  file guessed. The two inspections and the post-publish read-back now compare the release **id** and the
  pinned **title** as well as the tag and the flags, so a read-back that answers somebody else's release,
  or a retitled draft, fails; `.target_commitish` is deliberately *not* compared, because the API says it
  is unused for a tag that already exists and so proves nothing about the released commit. Three claims in
  v0.12.14 were overstated and are corrected here: the stored-digest match proves that the digest GitHub
  reports for an asset equals the staged bytes at the moment of the read — it does not identify which
  upload produced the bytes, and it says nothing about what a download later serves; the scripted `gh`
  cases prove the branches they exercise and nothing about GitHub's authorization decisions; and gh
  attaches its token to the upload host through hostname normalization *subject to the configured token
  and its own redirect safeguards*, which is narrower than “attaches”. One thing the review found that
  YAML cannot do: GitHub's **immutable releases** (generally available) lock a published release's assets
  and its associated tag afterwards and produce attestations, and GitHub recommends exactly the draft →
  upload → publish order this gate uses — but it is a repository or organization setting, so it is an
  operator decision on the board, not a step in this file. No tag has run the gate: still configured, not
  proven.
- v0.12.14 — a fifth deep review of the gate, over v0.12.13: the repair was right and four things around
  it were thin. The create response is now read as a whole, not just for an id: its `tag_name`, `draft`
  and `prerelease` must say what this run asked for before a single byte is uploaded, because a numeric
  id from a 2xx is not by itself this run's release. Its `upload_url` must start with
  `https://uploads.github.com/repos/{owner}/{repo}/releases/{id}/assets` — a non-empty URL from somewhere
  else is not a reason to post binaries at it. The release object's own `tag_name` is checked in **both**
  inspections and in the post-publish read-back: re-resolving the git ref proves the ref, not that the
  release still carries it, and the first review round found that association read only once, before the
  first snapshot. The release name is pinned to the tag (`name=`), because with `generate_release_notes`
  and no name GitHub invents a title that is not guaranteed to be the tag and composes the body from
  merged PR text. One claim in v0.12.13 was wrong and is corrected here: the workflow-write guard the
  Create-a-release note describes answers **404, sometimes 403 “Resource not accessible by
  integration”** — not 422 — and the same note sits on the update endpoint; whether it fires for a tag
  that already exists is documented ambiguity, not settled fact. What a fifth round could not close in
  YAML stays open and is named in §15: reads and a `PATCH` are not atomic, so tag protection plus a
  restricted set of release writers remains the only real control; and the cheapest thing that would
  falsify the rest is one canary run against the live API with the job's own token, which is an operator
  decision because it publishes for real. No tag has run the gate: still configured, not proven.

## 1. Overview

The Arboreal Inference Router ("AIR") is a .NET library + CLI that sits between
callers (agents, CLIs, tools, web clients) and one or more LLM inference
engines. It:

1. Exposes **OpenAI-compatible** `POST /v1/chat/completions` and
   `POST /v1/completions` (streaming and non-streaming), so any existing
   OpenAI client can point at it unchanged.
2. Provides **thread prioritization APIs** — logical threads (conversations /
   agents) carry priority, weight, and affinity; the router schedules requests
   against them.
3. Can **switch the underlying engine and model**. For a managed engine, configure the native
   Docker provider to replace the serving instance and check readiness at the engine endpoint
   (§5.3). The delegated exec provider remains available for legacy configurations.
4. **Enqueues requests that need different models** — a request declares model
   affinity (or capability); the router queues it until an engine serving that
   model is available (and may trigger a switch if configured to).
5. Configures engine support (startup/shutdown/health/model catalog) **without
   code changes**, via TOML files.
6. Runs under **Native AOT**: no runtime reflection on hot or trim-sensitive
   paths. Where reflection would be the default (JSON, TOML binding, DI-ish
   service resolution) we use source generators or explicit tables.

### 1.1 Future architecture (designed for, not implemented in v1)

- A separate **listener service** that connects to a **router host**; the CLI
  connects to the same host, starts one locally, or starts the listener.
- **Prefix-sharing-based prioritization across hosts** (sticky routing on
  shared prefixes).
- **Global request scheduling** optimizing latency and *impact on other
  requests* (prefill cost of uncached requests pushes them to least-busy
  servers with the model loaded).
- **Prefix _and_ suffix cache reuse** scheduling, once suffix reuse engines are
  in place (known fork capability; treat as an engine capability flag).
- A **Blazor web client** giving a live view of all inference with statistics
  and request logs.

v1 therefore keeps: a stable event/stat feed (the future Blazor client
consumes it), a pluggable scheduler interface, a pluggable "host/peer"
abstraction behind the engine set, and a config schema that can describe
remote peers without schema changes.

## 2. Non-goals for v1

- No distributed multi-host scheduling (only the abstraction for it).
- No suffix-reuse scheduling logic beyond capability modeling and scoring
  hooks (engines don't exist yet).
- No authentication beyond a shared bearer token (local tool).
- No model *transcoding* between incompatible API dialects (engines must be
  OpenAI-compatible already; llama.cpp `llama-server` and all current local
  backends are).
- No Blazor client in v1 (spec the feed it will consume).

## 3. Glossary

- **Engine**: a configured inference backend with an `openai-compatible` HTTP endpoint,
  a model catalog and optional health probe. A managed engine has a lifecycle provider.
  One engine definition can describe several models, each served by its own instance on a switch.
- **Model**: a served model identity (name + alias set + capabilities: context
  length, vision, coder, suffix-cache support). Engines expose which models
  they currently serve (`loadedModels`) and which they could serve
  (`catalog`).
- **Thread**: a logical request stream (conversation/agent) with identity,
  priority class, weight, and cache affinity. Requests are enqueued *on a
  thread*.
- **Request**: one chat/completion call, with model affinity, priority,
  deadline, and payload.
- **Router host**: (future) process coordinating engines and listeners; in v1
  the router runs in-process in the CLI server and is reachable via HTTP.
- **Listener**: (future) separate process exposing the same API surface that
  proxies to the host; the CLI can start or connect to one.

## 4. Architecture

```
+-------------------+        +---------------------------+
| CLI  (serve)      |------->| Router core (library)     |
| CLI  (client cmds)|        | - Priority queue          |
+-------------------+        | - Scheduler (pluggable)   |
        |                    | - Engine registry         |
        v                    | - Event/stats feed        |
+-------------------+        +------------+--------------+
| HTTP surface      |<---------------------+
| /v1/chat/completions, /v1/completions
| /admin/* (control), /admin/events (SSE), /admin/status
+-------------------+
        | (engine adapters: HttpClient)
        v
+-------------------+   +-------------------+
| strata :18021     |   | hyperqwen :18020  |  ... glm/deepseek via config
+-------------------+   +-------------------+
```

Projects:

- `src/Arboreal.InferenceRouter` — core library: domain types, priority queue,
  scheduler, engine registry/adapters, TOML config loader, client SDK,
  OpenAI DTOs. AOT-clean, no ASP.NET dependency.
- `src/Arboreal.InferenceRouter.Server` — ASP.NET Core (Kestrel) host exposing
  the OpenAI-compatible surface + admin surface; wires the core library.
- `src/Arboreal.InferenceRouter.Cli` — the `air` tool for serving, inference requests,
  model switching and status inspection (§11).
- `tests/Arboreal.InferenceRouter.Tests` — unit + integration tests (in-memory
  fake engine).
- (future, stubbed only) `src/Arboreal.InferenceRouter.Web` — Blazor live view.

## 5. Domain model (library API)

### 5.1 Threads and priority

```csharp
public sealed class RouterThread {
    string ThreadId;                        // caller-chosen, e.g. "agent-42"
    ThreadPriority Priority;               // see below
    double Weight { get; set; }             // 0.25..8 fairness weight, default 1
    string? ModelAffinity;                 // preferred model; requests may override
    ThreadCacheAffinity? CacheAffinity;     // normalized prefix hash handle
    DateTime CreatedAt; long Enqueued; long Completed; long Rejected;
}

public readonly struct ThreadPriority : IComparable<ThreadPriority> {
    ThreadPriorityClass Class;   // Interactive, High, Normal, Low, Background
    byte SubPriority;            // 0..255 within class (finer ordering)
    // Compare: class first, then subPriority; ties break FIFO (monotonic seq).
}
```

Priority ordering: `Interactive > High > Normal > Low > Background`.
Classes are open-ended (byte-coded, descending); new classes can be inserted
by number without breaking source compatibility. **APIs**:

```csharp
public interface IInferenceRouter : IAsyncDisposable {
    RouterThread RegisterThread(string threadId, ThreadPriority priority,
                                RouterThreadOptions? options = null);
    void SetThreadPriority(string threadId, ThreadPriority priority);
    ValueTask<RouterRequestTicket> EnqueueChatAsync(string threadId,
        OpenAiChatRequest request, RouterRequestOptions? options = null,
        CancellationToken ct = default);
    ValueTask<RouterRequestTicket> EnqueueCompletionAsync(string threadId,
        OpenAiCompletionRequest request, RouterRequestOptions? options = null,
        CancellationToken ct = default);
    bool TryCancel(string ticketId);
    RouterStats GetStats();
    IAsyncEnumerable<RouterEvent> SubscribeEvents(RouterEventFilter? f = null,
        CancellationToken ct = default);  // SSE feed source
}
```

`RouterRequestTicket` exposes: `TicketId`, `State`
(Queued|SelectingEngine|WaitingForModel|Executing|Completed|Cancelled|Failed),
`QueuedAt`, `Model`, `Engine`, and a `Task<OpenAiChatResult>`-style completion
handle plus streaming iterator. Cancellation is cooperative: dequeues when
still queued, cancels the upstream HTTP call when executing.

### 5.2 Requests

A request may declare:

- `Model` (exact name or alias) — **required** unless the thread carries an
  affinity and the request has none; if neither, config default applies.
- `Priority` override; `Deadline` (soft — used for queue admission and
  metrics; hard deadline → drop policy is configurable).
- `MaxQueueMs` — if not selected by scheduler within this window, fail
  (default from config).
- `CacheHint` — optional normalized prefix bytes/hash the caller knows is
  shared (see §8).

### 5.3 Engines (configured, not coded)

The initial deployment assumes system resources are dedicated to inference. Tools that start or
stop a router-managed model coordinate through its engine's single lifecycle owner. Host enrollment
requires verified enforcement of that ownership. Engine TOML controls per-container memory limits
and GPU assignments, with serving-profile overrides where the existing schema supports them. The
initial version preserves the installed `select-model` safeguards for the selected profile. A
dedicated host still honors configured limits and the defaults, overrides and activation gates in
§7. General resource-sharing policy is deferred.

Set `lifecycle_owner = "router"` and add an `[engine.<name>.docker]` table to manage local model
switches through the native Docker provider. The catalog lists the models and serving profiles
that the engine can run. A model or profile switch replaces the serving container with an instance
for the requested model and profile at the configured endpoint (§5.3.1, §7). Loading another model
inside the same instance is deferred. The external launcher's switch sequence is a behavioral
reference for native lifecycle.

The engine registry tracks the configured engines and the models reported by their endpoints.
`endpoint` determines where inference and discovery requests go. `lifecycle_owner` determines
who may change what the engine serves. `external` gives AIR no lifecycle authority, and AIR can
still serve requests for loaded models. `delegated` runs a configured selector for the bound
target. `router` binds the native Docker provider to the engine and its catalog targets. The
runtime copies the startup catalog entries, aliases, profile collections and selector arguments
and resolves the canonical target before claiming a switch. The native provider copies its
launch command and environment at startup. A provider supplied to the public runtime constructor
for a managed engine must report the configured kind. A concrete Docker provider must also match
the engine name, Docker router ID and configured daemon (§5.3.1). A mismatch fails construction
before provider I/O. The caller retains ownership of its supplied provider. An external engine
leaves the supplied provider unused (§5.5, §7).

```csharp
public sealed class EngineDefinition {         // from TOML §7
    string Name; EngineKind Kind;              // v1: OpenAiCompatible
    Uri Endpoint;                              // e.g. http://127.0.0.1:18021/v1
    string? ApiKey; string? ApiKeyFile;        // inline or file path
    EngineLifecycleOwner? LifecycleOwner;      // External, Delegated, Router or infer legacy ownership
    EngineLifecycleKind Lifecycle;            // Docker or legacy delegated exec
    DockerLifecycleConfig? Docker;             // native provider settings
    bool Remote;                               // legacy true maps to External
    IReadOnlyDictionary<string, EngineModelDefinition> Catalog;
    HealthProbeDefinition? Health;             // probe + readiness predicate
    EngineCapabilities Capabilities;           // vision, suffixCache, slots...
}
```

**Legacy exec compatibility.** With no Docker table, a usable catalog selector or an explicit
`lifecycle = "exec"` gives a legacy engine delegated ownership unless `remote = true`.
A catalog entry's legacy `select = { exec = … }` runs the configured selector via
`Process`. The selector is responsible for stopping and starting serving processes. The router
serializes switches per engine and uses the selector's exit status before checking readiness.

- Argument substitution expands `{{model}}` to the canonical catalog name and `{{profile}}` to the
  target or default profile name (empty if unset). `{{suffixCache}}` expands to an empty string.
- Args are re-quoted with `CommandLineToArgvW` rules before being handed to
  `ProcessStartInfo.Arguments` (the parser .NET uses on Unix too), so a single
  configured arg can contain spaces, as in the legacy shell argv `args = ["-c", "select-model {{model}}"]`.
- The provider drains stdout and stderr concurrently with the exit wait to keep a full pipe
  from blocking the selector. A nonzero exit fails the switch.
- `select.wait_timeout_ms` defaults to 7,200,000 ms. A selector timeout kills the process tree
  (`Kill(entireProcessTree: true)`) and reports `timeout_after_<ms>ms` on `engine.switch_failed`.
  The value `-1` gives the selector process an unlimited exit wait.
- After a successful exit, the router starts a separate readiness budget from
  `select.wait_timeout_ms`. A value of `-1` expires this budget before its first read.
  Use a positive value to allow post-exit readiness. Even with `select.wait_health = false`,
  a fresh model-list response must report the requested model. The runtime clears the
  switching state when the attempt ends (§5.5).

#### 5.3.1 Container-native lifecycle (partly implemented)

**Provider configuration.** Use the Docker engine and catalog declarations in §7 together.
The loader selects Docker whenever the `[engine.<name>.docker]` table is present, even if
`lifecycle = "exec"` was specified. It rejects a Docker engine if any catalog entry carries a
legacy `select` block. The Docker table's image, command, environment and port binding apply to
every target on that engine. In `command` and `env`, `{{model}}` is the canonical catalog name,
`{{profile}}` is the target or default profile name (empty if unset), and `{{endpoint}}` is the engine
name, such as `strata`.

Give each router sharing a daemon a distinct `router_id`. Published ports default to loopback
(`host_ip = "127.0.0.1"`). `remove_stopped` defaults to `true`, so switched-out containers are
removed. `stop_timeout_ms` defaults to 30,000 ms and sets the Docker stop grace in whole seconds
(minimum one second). `call_timeout_ms` defaults to 300,000 ms per Engine API call, independently of
the readiness wait after launch.

**Shipped provider seam.** `EngineLifecycleCoordinator` calls `IEngineLifecycleProvider`
in order: `OwnedPeersAsync`, validate-only `PreflightAsync`, `StopAsync` for owned peers if any,
then `LaunchAsync`. The implementations are `DockerLifecycleProvider` and the legacy
`DelegatedExecProvider`. A `LifecycleResult` carries a reason for the event feed and, on refusal,
an actionable operator message. The runtime performs readiness checks after launch according to
the provider's `ReadinessPolicy`.

Docker stamps `arboreal.router`, `arboreal.engine` and `arboreal.model` on each container it creates.
Peer discovery filters by both router and engine labels and rechecks both locally. Only matching peers
reach the stop step, which uses container IDs. An ownership-query failure aborts the switch. The
coordinator's semaphore is per engine. The label query neither detects nor reserves a host port held
by a sibling engine, another router or an unrelated container. A conflict on `host_ip:host_port`
can therefore make Docker create or start fail after the owned incumbent has stopped.

The concrete provider captures the engine name, router ID and daemon before creating its HTTP client.
Startup binding compares the label values and Unix socket paths with ordinal equality. A bare Unix
path and the `unix://` form of the same path compare equal. HTTP(S) transport compares the complete
canonical absolute client base URI. An HTTP endpoint cannot match a Unix socket even if it uses the
same synthetic HTTP base. Invalid daemon declarations fail locally. This verifies configured
transport and label scope. Physical host or serving-instance identity, remote daemon authenticity,
router-ID uniqueness, filesystem or DNS alias equivalence, launch settings and exclusive host
occupancy require separate evidence. Custom providers and supplied HTTP handlers are trusted
in-process components.

Docker preflight checks daemon access, the configured API version and local image identity before
any stop, removal, create or start. It looks up `docker.image` on the daemon and checks three pins
against the inspect response. `image_id` must match `Id` as a full `sha256:` digest with 64 hex digits.
`image_repo_digest` must appear verbatim in `RepoDigests` as a complete
`repository@sha256:<64 hex digits>` entry. `image_platform` must match the reported
`Os/Architecture[/Variant]`. A pin without a variant matches an absent or empty reported variant.
The image ID and repository manifest digest describe different objects.

Tag-only Docker configurations still load. A native switch requires all three pins. The loader
rejects a partial or malformed set. Missing or mismatched inspect evidence refuses preflight with
one operator action. Images built or loaded locally may report no named repository digest.
An image without that evidence cannot pass native verification. The router never pulls an image.
Create uses the configured full image ID. If that ID disappears after preflight, create fails
without trying the lookup reference. A direct provider launch also requires the pins and uses the
full ID. The coordinator supplies repository and platform verification by running preflight.

**Resource projection.** When configured, the checked resource settings in §7 populate
`HostConfig` in the same create request as `PortBindings` and `NetworkMode`. The pinned image,
command, environment, labels and stop timeout keep their existing meanings. HIP uses exact
same-path Docker `Devices` mappings with `rwm`. NVIDIA uses one `DeviceRequests` entry for
all GPUs or ordered device IDs. Structured bind mounts request the existing daemon-host
source and its explicit access mode. CPU, memory, total memory plus swap, shared memory
and ulimits reach their typed Docker fields.

Resource-bearing requests are available for offline validation and projection. Provider
preflight and direct mutators refuse with `docker_resources_not_qualified` before any
Docker write. Enrolled and injected lifecycle activation also refuse resource-bearing configs.
Omitting resources preserves the previous switch and create behavior. The measured HIP Qwen
archive and the unqualified Bonsai Q8 script supply distinct offline fixtures. A passing
projection establishes the requested Docker fields. Activation requires host-bound authority,
effective resource inspection, artifact and live instance identity, and reconciliation of
an uncertain create while the host reservation is held. It also requires verification of
read-only bind enforcement for nested mounts.

Native serving tiers can declare literal commands, environment, mounts, memory limits, a single
NVIDIA device, shared memory and Docker health checks for offline create projection. Each of
these settings gates engine activation and native mutations across the engine's catalog. The Docker provider refuses with
`docker_target_not_qualified` before any write.
A coordinated switch may first read ownership.
The resolved payload describes launch intent. Serving readiness, host authority and exact-instance
request delivery require separate proof. Docker health check metadata and status grant no AIR
readiness, capacity or admission authority.

An engine-wide entrypoint override or host IPC declaration also requires offline-only validation.
Either one prevents runtime startup before provider binding or enrollment. The provider checks
unqualified serving tiers first, then launch options with `docker_launch_options_not_qualified`,
then unqualified resources. All three refusals precede Docker writes. A coordinated switch may
first read ownership.

A preflight refusal leaves existing containers untouched and can retain a still-valid incumbent
discovery proof (§5.5). The provider refuses unavailable or unauthorized daemon access without
escalating. Once stop begins, a failure can leave some peers stopped or
removed. A create, start or readiness failure can leave the target container behind. The shipped
path has no rollback. The scheduler considers only healthy
engines for an automatic switch, so `auto_switch` alone cannot load the first model on a cold engine.

On the non-enrolled Docker path, the post-launch readiness budget comes from
`docker.wait_timeout_ms` (default 600,000 ms). A value of `-1`, zero, or any negative value
expires before the first readiness read. Use a positive value to give the new process
time to prove readiness. With `wait_health = true` (the default), readiness requires a
passing health probe and a model list containing the canonical target. With
`wait_health = false`, the runtime still refreshes the model list and requires the target.
A named profile requires a fresh measured window bound to that model and large enough
for the profile. The non-enrolled runtime does not match reported total slots to the
profile. Its probes reach the configured endpoint without an exact-container binding
(TASK-118). The native provider's verification uses fake providers and fake Docker HTTP
responses. A live model swap has its own acceptance work (TASK-112).

**Target container-runtime interface.** The remaining design adds a fakeable, cancellable
`IContainerRuntime` below the provider seam:

```csharp
public interface IContainerRuntime            // proposed Docker Engine API adapter
{
    Task<ContainerInfo?> Inspect(string name, CancellationToken ct);   // null IFF 404
    Task<IReadOnlyList<ContainerInfo>> List(string labelSelector, CancellationToken ct);
    Task<ImageInfo?> InspectImage(string pinnedRef, CancellationToken ct);   // no pull, ever
    Task<string> Create(ContainerPlan plan, CancellationToken ct);
    Task Start(string id, CancellationToken ct);                 // 204/304 both fine
    Task Stop(string id, TimeSpan grace, CancellationToken ct);   // daemon kills after grace
    Task Kill(string id, CancellationToken ct);
    Task Remove(string id, CancellationToken ct);
    Task<ExecResult> Exec(string id, IReadOnlyList<string> argv, CancellationToken ct);
    IAsyncEnumerable<LogFrame> Logs(string id, CancellationToken ct);
    IAsyncEnumerable<ContainerEvent> Events(CancellationToken ct);
}
```

**Transport and AOT.** The shipped provider uses raw Docker Engine HTTP over a Unix socket or an
HTTP(S) daemon address, with proxy and redirect handling disabled, per-call timeouts, a configured
API version and source-generated JSON (§9). AIR declines Windows Docker named-pipe transport because
the provider has no qualified Win32 pipe connector. A router-owned Docker declaration with `npipe:`
or a bare Windows pipe address fails validation before provider binding or I/O. Direct construction
and scope checks on a Docker provider apply the same refusal. Windows CLI builds support other AIR
commands under their existing contracts. Version negotiation and daemon events remain separate work.

**Polling for a tracked Docker launch.** After the concrete Docker coordinator receives start
`204`, an exact inspect establishes a baseline for that launch. It checks the immutable container
ID and image, the router, engine, model and profile labels, and coherent running state with a
full-precision start time and restart count. A second exact match after the endpoint reads is
required for the first readiness proof, including with `wait_health = false`. It must fit the
original readiness budget and the bounded Docker observation. Local Stop/DELETE issue and proof
publication are ordered by the provider gate. Missing baseline or failed confirmation leaves
readiness unknown without restoring the previous incumbent or allowing an endpoint-only proof.

The existing serial health timer (`health_probe.interval_ms`, default 2,000) then polls the exact
container even while the engine is idle and when the endpoint fails to answer. Inspect uses the
pinned daemon/API and GET only, with `docker.call_timeout_ms` bounding inspection and gate waits
and a 256 KiB inspect body ceiling. A valid contradiction withdraws health, serving capacity and
switch proof for that exact launch and retains the last roster as history. A fresh matching
sample and successful endpoint round can recover from a transient unknown without restoring the
old switch proof. A definite contradiction remains until a new qualified `204` launch. The
poll rechecks pending and previously issued local Stop/DELETE before a positive publication and
never settles or retries a mutation. A timed-out read that later returns can only clean up.

Polling covers tracked launches at completed samples. Startup-existing containers, initial
`304` and other direct creates lack this baseline. Between-sample changes, changes to the
application process inside a matching container, and authentication of the connected serving peer
remain separate. This supplies no host-wide vacancy, retirement or mutation authority.

**Internal Docker exec and logs.** The native provider inspects the full container ID on the scoped
daemon and checks the exact returned ID and both ownership labels before exec or log capture.
Exec passes literal argv, attaches stdout and stderr without stdin, privilege elevation or TTY, and
uses HTTP/1.1 attachment without Upgrade. Logs requires an explicit non-TTY container and takes one
snapshot of at most 256 tail lines with both streams enabled. Both read 8-byte headers and binary
payloads into ordered stdout/stderr frames. A capture keeps at most 64 KiB of payload and 256 frames.
An empty frame takes one frame slot and zero payload bytes. Dropping a frame or any part of its
payload ends the retained prefix. The reader continues validating and draining up to the 1 MiB
framed-wire ceiling, including headers. A malformed or oversized stream refuses.
Container inspect JSON is capped at 256 KiB, exec create at 4 KiB, exec inspect at 16 KiB and error
bodies at 4 KiB. The one `docker.call_timeout_ms` budget begins before the shared provider gate and
covers every response read through terminal inspect.

Exec create requires HTTP `201`. Container inspect, attached start, logs and exec inspect require
`200`. After a clean attached EOF, exec inspects the exact exec and container IDs and requires
explicit `Running=false` and a nonnegative exit code. Zero succeeds and nonzero fails with the
confirmed code and bounded output. A frame-boundary EOF alone cannot establish that all upstream
output reached the reader. Failure to establish terminal evidence leaves exit unconfirmed.
Interrupted capture issues no automatic retry or compensating write. A pending Stop/DELETE still
blocks exec before any daemon request. These internal operations have fake HTTP coverage and no
runtime, admin, CLI or readiness call site.

**Target ownership and launch checks.** Host-wide occupancy and ownership reconciliation remain
target design. The per-engine image check above verifies the daemon's local image mapping.
Registry trust, host vacancy and serving-instance identity require separate proof. Effective
resource enforcement requires daemon inspection under the host authority. A create warning
or a mismatch in inspected resources must prevent start and keep the fence through exact-ID
cleanup. A lost create response requires reconciliation before a successor can mutate.

**Shipped Docker stop-conflict handling.** The ordinary stop acknowledgments are `204` (stopped),
`304` (already stopped) and `404` (absent). After a stop returns `409`, the provider inspects the
original full 64-hex container ID on the same daemon and pinned API. Inspect `200` must report the
exact ID, both bound ownership labels and an explicit exited state with running, restarting, paused
and dead all false. Inspect `404` proves absence and skips DELETE. After a verified exit,
`remove_stopped = true` uses non-force DELETE and accepts `204` or `404`. A restart conflict, an
unavailable or invalid observation, or an unexpected stop, inspect or removal response refuses
replacement in that attempt. With removal disabled the provider still requires exit or absence
before proceeding after `409`. These conflict-derived observations do not train shutdown timing.

**Shipped provider-lifetime stop and removal recovery.** Before sending stop or DELETE, record
the operation and the full container ID. An unresolved outcome blocks new writes through that
provider. Only exact-ID inspect `404` on the same configured daemon and API followed by fresh
ownership inventory `200` with a valid array omitting that ID releases the fence. Both reads and
release run under the gate shared with direct and coordinated writes. A present but exited
container and unavailable or malformed readback keep the fence held. Recovery compares full ASCII
hex IDs without regard to letter case and uses the submitted spelling in the exact request. The
same-attempt stop-conflict inspect and ownership labels retain ordinal comparison.

The runtime rechecks prior issued Stop/DELETE of its incumbent, including acknowledged outcomes,
under the same provider gate at the restoration commit. If the provider still has a held operation
or issued a stop or removal of the incumbent, the runtime leaves capability unknown. A read-only
refusal can still restore an incumbent whose proof passes those checks and the runtime's existing
probe checks. Independent create/start can affect the serving endpoint without recording incumbent
stop or removal. The gate gives no restoration guarantee for that launch. Post-publication direct
write authority, uncertain create/start settlement, durable recovery after process loss and
whole-host exclusion require separate evidence. Exact scoped absence does not prove any of them.

**Target host transaction.** Reserve the host, prove ownership and admissibility, and validate
assets, receipts, qualification, image identity and privileges. Close admission and drain active
requests, revalidate preflight evidence, then stop only owned peers and launch the
exact target. Bind container operations to immutable IDs. Verify the serving instance, health,
canonical model, exact context and slots, and required modalities, including provider-exec checks,
before publishing readiness and reopening admission. A competing target waits for the host.
Same-target callers can join the active switch, and cancelling one waiter detaches only that
waiter (§5.5). The host reservation and
restart reconciliation are pending (TASK-110), as are the admission reservation and drain (TASK-25).

**Native target quarantine and recovery (decision 62).** Once a non-enrolled native switch
enters its lifecycle transaction, a selected failure latches its engine, canonical model,
effective profile and bound definition in the runtime. This covers ownership and preflight
refusals, an unsuccessful provider result or provider exception, and failed readiness.
An earlier claim-hook or scheduling failure rolls back with zero launches and leaves no
target latch, so a later attempt can proceed without a reset.
The native plan resolves an omitted tier or alias to the same canonical tier used in Docker
create. A read-only refusal can preserve an incumbent when the same provider and current
probe evidence still support it. Subsequent attempts to switch to a latched target return the
recorded failure category without starting another provider transaction. Dispatch wakes,
elapsed cooldown and unrelated reloads leave the latch in force. Independently proven
resident service and unrelated eligible targets can continue.

Each affected selected ticket settles once. Before response commitment, it receives
`503` with `switch_failed`. On a committed stream it receives the existing in-band
error (§6). A waiter that joined a failed switch keeps its recorded outcome even when an
operator resets that target and a later switch proves it ready.

An explicit operator reset clears a single latch on the current definition (§7.7).
After the incumbent stops, a host that can hold only one resident inference instance needs
a fresh ownership-checked transaction and verified readiness to serve another target.
Uncertain occupancy continues to block launch. The ordinary host and provider authority
checks govern any attempt after reset. Alternate recovery remains separate work (TASK-114).

**Reservation semantics.** Today a dispatched ticket holds its engine-slot reservation across a model
switch (`RouterCore` reserves at dispatch and releases in the `finally` after the engine call). That is
defensible while an engine serves exactly one model — there is nothing else it could answer — but once
the router performs the drain itself the switch must be an explicit *lifecycle* reservation, not
inference capacity, or the scheduler will read a switching engine as a busy one.

**Host reservation foundation (TASK-110).** An exclusive handle serializes cooperating processes that
open the same stable local file. The fixture provisions a checksummed genesis record and retains the
file and directory across restarts. A missing, empty or unreadable record refuses mutation. The
coordinator flushes each new owner and epoch and persists the operation ID and target before stop or
launch. It flushes `Prepared` and then `Dispatching` before sending the operation. For stop, it writes
both records before withdrawing the incumbent's serving authority. Recovery can discard `Prepared`.
`Dispatching` blocks a successor epoch until the provider supplies a matching terminal outcome.
This file protects ordering among its holders. Host authority, store identity, and protection against
restoring earlier checksummed contents require independent evidence.

**Internal qualification path.** Tests bind the already selected lifecycle target to a fake host
controller. The coordinator closes the supplied admission guard for vacant and occupied groups and
rechecks vacancy before launch. For an occupied group it repeats preflight after drain and stops only
the freshly observed, owned immutable ID. The fake verifies the launched instance and commits
readiness through the guard while the reservation is held. A fake worker records acceptance before
its simulated create. Receipts match the full operation, including its ID, epoch, action, target and
launch plan digest. Applied stop also names the exact stopped instance. A rejection before issue
can settle an intent. An absent, mismatched or uncertain receipt leaves the operation fenced.

An applied launch records the returned immutable instance ID through exact-plan readiness. A new
coordinator can recover from a lost reply by reading the worker's durable receipt. It re-observes
the recorded instance under a closed admission guard, then verifies it without issuing create again.
Only the same requested plan can reuse that readiness. A different target must take a fresh epoch
after recovery, and an unproved instance retains the fence. The fixture does not persist route
publication. Foreign or unknown occupancy refuses the switch without a provider fallback.
Public configuration and the request dispatcher do not construct this binding. The dedicated
single-engine restriction for explicit native ownership still applies.

**Offline successor checkpoint (TASK-110).** The standalone store uses a separate stable lock and a
versioned, bounded effect checkpoint. Only explicit provisioning creates genesis. Reopen requires a
valid installed checkpoint. Old applied snapshots, route stages and temporary files cannot reseed it.
Each transition checks the installed predecessor under the lock, flushes a complete replacement and
installs it atomically. A failed or ambiguous write fences that handle. A new holder reads the actual
installed version. These guarantees cover cooperating processes and trusted local storage through
process death.

One checkpoint retains the incumbent's exact launch and binds the next epoch, exact stop and frozen
successor plan before dispatch. Prepared stop may abort before dispatch. A dispatched stop can retain
the incumbent only with a matching before-issue rejection and an independently supplied fence against
delayed delivery. An applied stop needs the exact receipt and independent stopped-instance evidence.
Only that phase can prepare the recorded successor, and its applied launch needs the matching receipt
and independent operation-to-instance binding. Unknown effects preserve the same dispatch intent.
A settled resident's launch epoch equals the checkpoint epoch. A retained or transitioning incumbent
can come from an earlier launch epoch. Every applied launch after the initial one retains its matching
predecessor stop. The standalone store checks evidence identity and records the validated disposition.
Its caller must obtain an independent effect or rejection fence from the authority that can prove it.
The checkpoint contains no evidence provenance for physical authority or takeover audit. Worker I/O,
runtime routing and admission remain on their existing paths. Production enrollment is disabled.

The initial-launch fixture compares the selected plan's exact serialized Docker create request with
an independently projected approved payload before dispatch and when reopening. Its local history
binds the submitted bytes and the checkpoint operation to anchored dispatch, worker acceptance and
an exclusive may-have-issued decision before fake daemon I/O. The issue fence compares current
partition policy, owner and closed-ingress evidence through daemon entry. The observer reads
separately seeded fake daemon effective settings, assigned devices and running-instance evidence.
It verifies exact settings, both devices and matching create and start IDs before recording Applied.

A new opener compares local ancestry with a separate modeled monotonic head and reads the original
receipt bytes for the exact operation and instance. The journal and fresh ingress evidence feed an
otherwise-eligible fixture issuer. Closed or stale authority refuses minting and writes. A separate
current open observation permits the matching control send. An older restore, an unheaded issue
decision or delayed stale delivery grants no issue. If completion stops after applying the checkpoint
or after appending the journal and before advancing the head, the fixture quarantines the installed
evidence and refuses receipt lookup and ingress. Sequential repeat and competing requests add zero
fake create or start calls in these refusal cases. The fixture models the head and observer with local evidence roles.
Host exclusion, administrator credential isolation, directory durability, split reconciliation and
an authenticated production head require separate proof.

**Offline coordinator receipt path.** Both switch wrappers require a positively commissioned legacy
slot before legacy settlement, recovery or epoch allocation. The legacy reservation frame must carry
root `Epoch`, `Owner` and `Pending` fields, including explicit nulls when applicable. The reader
refuses an enrolled envelope or incomplete state before legacy epoch allocation. The enrolled
receipt entry takes its scope from a separate trusted catalog. Restored local bytes cannot change
the selected authority root. Unknown, inconsistent or partly installed scope refuses closed. An
enrolled grant without a bound operation stays fenced and cannot allocate a second epoch.

One coordinator operation binds the canonical intent and exact approved create payload. Its stable
lock and installed reservation record are separate. Under the modeled issue fence, the worker rereads
that installed record at acceptance and before issue. Both reads match the anchored dispatch and
current independent policy. Acceptance and the exclusive may-have-issued decision precede fake I/O.
Immediate launch, reconciliation and saved Applied reopen share the same enrolled validation of the
anchored operation, intent, exact instance and separate effective create and running observations.
Incomplete or contradictory evidence retains the effect fence. A verified result is `AppliedIngressClosed`.
It returns before readiness, switch success, route publication, Open or any permit. Exact historical
lookup can still return the original receipt after authority changes. A fresh acknowledgement requires
current owner, policy and closed ingress. These code paths are exercised with fake authority only.
Public enrollment remains disabled.

The internal supervised producer uses this same closed-receipt entry for DeepSeek `32k` or Qwen
`256k-4`. The enrolled coordinator assigns the operation and epoch. The writer anchors the full
commissioned head and member mapping with the frozen intent, then anchors acceptance and exclusive
possible issue before entering the typed create port. It compares independently read current
commissioning against the original at acceptance, issue, start and fresh acknowledgment. An
independent observation binds the exact wire bytes to the operation, epoch and daemon. The producer
records their digest only after the bytes match the approval and separately observed effective image,
process, resource and route settings agree. The current journal format records the verified full
container ID, exact operation, create digest and independently commissioned daemon, socket and API
version in revision 4. The writer reads back its anchored record before it issues Docker Start.
Revision 5 Applied requires the acknowledged start and matching independent create and running
evidence. The receipt and its serialized bytes bind the same operation and container ID as revision 4.
A missing start acknowledgment keeps the exact operation and container ID fenced through reopen.
Recovery issues no replacement or compensating write, and a running observation alone cannot
settle the operation. Legacy revision 4 Applied remains available for historical lookup. An older
Issued-only history stays held.

Contradictory or malformed evidence keeps the installed effect fenced. If the head refuses to
advance after a journal flush, the producer preserves the extra frame and old head. Reopening a split
journal keeps the slot quarantined with no receipt, automatic repair, resend or successor. Historical
lookup reads the original bytes from a coherent anchored journal and does not grant fresh authority.
The producer is qualified with offline ports and a modeled independent head. Service deployment,
durable host commissioning, authenticated live readiness and serving require separate authority and
evidence.

The successor ledger fixture records submissions, worker acceptances, daemon attempts and applied
records in separate journals. These are synthetic evidence roles and can share a fixture process.
The fake worker holds one fence through the issue decision and daemon entry. A durable never-issue
record bars delayed delivery and lets the worker finish a missing canonical rejection terminal under
that fence. A contradictory terminal remains installed. An issue decision with no terminal reply blocks
retain-A and further dispatch. Applied recovery matches the terminal and fake daemon effect. Launch
recovery also reads installed residency and checks the complete launch, group and enrollment of the
non-foreign resident. Stops compare the exact incumbent and creates recheck vacancy at fake daemon
entry. Records stage in unique temporary files and publish atomically. Reopen ignores unpublished
records and holds on corrupt installed evidence. These controls exercise subprocess death in trusted
fixture storage. They supply no production authority or inference permit evidence.

The successor vacancy fixture models one enrolled partition with a separate source of partition
facts. Exact incumbent ownership and causal retirement permit the prepared stop to progress. Before
a successor submission, a new query must prove complete coverage and vacancy for the enrolled
partition. The query binds the enrollment, controller and worker generations, the incumbent's full
immutable launch operation and plan, the exact checkpoint stop effect, the transition epoch and the
frozen successor plan. The owner and retirement evidence bind that same launch. Under the same fence
as every modeled writer, the worker compares the installed stop effect and current coverage and owner
generations before daemon entry. A change and restore invalidates the old observation. Missing
preflight proof leaves `StopApplied` without a successor
submission. Refusal at daemon entry can leave a durable submission and acceptance, with no attempt
or applied effect. Prepared stop and launch requery on reopen and keep their recorded identity.
Reopened dispatch reconciles only the exact operation. A recovered stop requires a new vacancy
query. A recovered launch uses its exact terminal and effect. The retirement and partition evidence
are synthetic. Production enrollment remains disabled.

**Offline first write (TASK-130).** The issuer transfers an authenticated application stream
bound to one invocation. The transport validates the route and invocation before connecting its
HTTP/1.1 serializer. The first nonempty application write presents the exact grant for that stream.
The single-host issuer fixture permits one checkout per invocation and consumes the issued grant
once. It rejects an unissued grant even when the invocation and stream match. The exchange closes
the grant it receives and awaits terminal permit cleanup. The issuer owns the cleanup of a grant
it withholds. In the substitution fixture, the issuer explicitly revokes and closes the withheld
original grant, and the peer observes EOF with zero bytes. Production enrollment still requires
frozen request body and authorization binding, durable issuance and authoritative physical
retirement.

The offline causal-drain fixture uses the verified transport and a surviving issuer with one
generation. The issuer orders closure and first-write authorization under one gate and revokes
an otherwise authorizable prepared grant. The open control sends its exact bytes with one permit.
Closing first sends zero bytes, acquires no permit and seals the granted channel. An authorized
write paused before backend delivery remains a physical use after the synthetic caller timeout.
The fake backend authors terminal evidence from the delivered request and its independently seeded
channel identity. A backend-authored grant mismatch cannot release the permit. The successful
no-more-write seal and matching terminal retire the use once. A fresh controller
queries the surviving issuer after its predecessor dies. Unresolved retirement leaves the exact
stop prepared and issues nothing. Before dispatch and at daemon entry the fixture checks the
installed closed-frontier witness and independent owner evidence. After the stop it still needs
fresh vacancy to create the frozen successor. The replacement route remains closed. Serving process
identity is checked for consistency within the issuer's fixture binding. Independent process
attestation remains a production host and verified-dispatch requirement. Enrolled admission, issuer
crash recovery and live host authority remain outside this qualification.

The offline host coordinator fixture can start a fresh switch or recover a matching applied
launch. A fresh switch preserves the old route through untouched preflight. Exact applied-launch
recovery closes the old route before the first occupancy query, including when that query cannot
prove the replacement. The fixture stages a closed route against the applied launch and its
original plan. The guarded state decision runs with minting closed, and completion runs outside
the short guard. Publication refusal, cancellation, staging I/O failure, failed completion or
failed drain disposal leaves the routes closed and retains the applied launch. A new coordinator
can reverify that launch and stage a fresh generation without another stop or create. Only
explicit Open of the still-current completed generation enables the test issuer to grant a permit
and send. The applied record remains after success. Enrolled runtime, deadline enforcement and
production activation remain outside this qualification.

The internal deadline fixture bridge takes the timeout from the validated applied launch plan.
One attempt starts before the exact post-drain identity check and reserves its closed route
before reading instance-bound evidence. The bridge stages that route before calling the runtime
guard. A committed decision must return the same proof stored for the closed route under the
guard. Its runtime attempt and full target must match the claim's captured identities before delivery.
The guard may use the closed-route and Open callbacks once, during their synchronous calls.
A valid decision survives completion, cleanup or notification faults, and the final result
records whether the route opened. Completion notifications can arrive while minting is closed.
After cleanup, only an accepted exact Open sends the definitive dispatch wake. A recovery request
cannot keep a superseded route open by naming a different target. The recovered-launch fixture uses the
runtime waiter and terminal path with injected bound evidence. It exercises an in-budget
commit, one timeout at equality without timer delivery, and fresh verification of the same
applied launch. Ordinary enrolled intake retains its capacity guard and production activation
remains gated.

The offline DeepSeek/Qwen companion uses independent dedicated configurations for
`deepseek-v4.1-flash-1017-iq2-xxs` at `32k` (32,768 tokens, one slot) and
`qwen3.8-flash-next-iq3_s` at `256k-4` (256,000 tokens, four slots). Both capture the
ordered create payload from separate selector-derived settings and bind the exact applied
instance and plan to a fake host receipt. The DeepSeek readback uses the Maya `/v1` completion
base, absolute `/v1/models` loaded-row discovery and absolute `/props` serving gauges.
Its endpoint changes the frozen plan digest without changing Docker create settings.
The fixture refuses wrong image identity, wrong canonical model and a measured window smaller
than the selected tier requires. It exercises the enrolled runtime guard with fixture-supplied
instance observations. Each direction checks a canonical POST through certified checkout on
the current recorded channel. Recovery retains the applied receipt and opens a fresh
incarnation. The old incarnation cannot mint another grant or authorize the first write of
a grant it received before closure. Two explicit router-owned engines in one configuration
still fail the dedicated-engine topology check on all public configuration paths.

Internal offline preparation checks the selected pair against one independently supplied
current topology head for the commissioned slot. The head binds the partition, daemon,
router owner, enrollment generation, revision, digest, members and device grants. Before
either Docker projection, both members must match the exact commissioned identities, routes,
socket paths and endpoints. The checker projects the approved targets and compares their
plan digests, then returns an immutable descriptor with exact Docker create settings, image
pins and original readiness budgets. The descriptor provides no runtime, lifecycle or serving
authority. Native host enrollment and a live Pi swap remain separate acceptance requirements.

The internal offline commission client reads one reply over a Linux Unix-domain socket with
TLS and a supervisor-pinned observer certificate. It requires the exact slot and a fresh
challenge. The length-prefixed request and reply have 1 KiB and 512 KiB limits respectively.
A complete reply is usable while the peer remains open. One five-second monotonic deadline
and the caller-linked cancellation token cover the exchange and a cooperative asynchronous
read of the independently provisioned full commission. Before parsing the internal intake's
TOML input, the claim must match the complete current head and the observer's scope, head,
grants, devices and approvals must match the independent snapshot by value. Projection uses
that independent snapshot. Both targets must match its approved image, plan and exact create
bytes, with a pinned API version for each. Expiry or cancellation before the closed result
refuses it. Intake has no launch journal, coordinator or engine runtime.
Public startup and reload still apply the dedicated-engine topology guard. The independent
head reader verifies the current records snapshot against a separately protected committed
floor. Deployment requires that floor to resist rollback independently of the records and
the router, with separately commissioned observation and host providers.

In the older injected immediate-host fixture, proof publication remains provisional while the
switch marker is raised. The host records acceptance with a synchronous store under the runtime
transition lock. A stop before that store vetoes acceptance. A stop after it can still win the
runtime terminal decision. Completion or a returned refusal
selects the terminal outcome under the same transition lock as stop. The selected outcome
is notified outside that lock, and a later cleanup failure cannot replace it. A stopped runtime
refuses first-send admission even if the fake host already recorded acceptance.

**Offline verified dispatch (TASK-118).** An injected resident proof binds the committed
route generation, exact instance and serving incarnation, launch epoch, captured configuration
and effective Docker plan, canonical model and tier, measured window and slots. Parsed Docker
create settings and the image manifest/platform pin describe launch intent. Projection uses
the captured config and binds its effective endpoint. The independently verified fixture resident
supplies matching instance evidence and measured capacity. Resident reuse requires the same
engine and canonical model as well as the resident's exact tier. A retired
generation stays closed. Only a fresh verified availability publication can rearm a spent
transient preparation and wake dispatch. The issuer orders closure against authorization for the
first nonempty application write. An authorization that wins remains counted through retirement.

The exact ticket's preparation freezes the method, effective URI, JSON media type, rewritten
UTF-8 body and upstream credential policy including its epoch. Validation rejects malformed,
duplicate and case-shadowed model fields before rewriting. A present raw model must match the
captured requested spelling under the catalog's case-insensitive comparison. An omitted model
uses the captured resolution. The rewritten object must name the exact canonical model once.
The offer admits only the bound Authorization and optional matching Host request headers and
the exact application/json content type. A disagreement refuses before checkout. Only the
frozen request values reach the verified serializer. The same transport worker exposes the
primary result and typed terminal evidence. Caller cancellation reaches the pending header
read on the borrowed channel. The worker retains responsibility for grant and permit cleanup.
A submitted use releases once after conclusive no-transfer, confirmed grant return or
issuer-confirmed retirement, together with successful preparation cleanup. Ambiguous checkout,
unconfirmed issuer retirement and failed preparation cleanup preserve the count even after
logical failure. Stream close and permit retirement independently contribute issuer evidence.
A response-disposal failure is reported even when issuer retirement is confirmed. When
preparation cleanup succeeds, the use still releases.
The verified path reports one redacted cleanup event for faults or unconfirmed retirement
and preserves the original logical result. Standalone exchanges preserve each observed
cleanup exception alongside any primary send failure. Engine-local stop authority has no
issuer bridge in this fixture.

**Production evidence.** The raw Docker inventory can report known foreign containers. Even an
empty inventory leaves vacancy unproven, and raw Docker supplies no terminal receipt for an
unacknowledged router operation ID. Positive authority requires independently verified enrollment,
operating-system enforcement over the full claimed residency partition, and durable operation
receipts that exclude delayed effects across provider restart. A partial partition supports a claim
only for the resources it covers. Keep unresolved mutations fenced until an authoritative receipt
or reconciliation settles them.

The production handoff must preserve a still-valid incumbent after a proved pre-mutation refusal
(decision 62). The internal fixture preserves the physical incumbent at preflight and withholds
runtime restoration pending instance-bound evidence. Production inference must acquire an
instance-bound permit before writing upstream. The permit lasts through response disposal and
remains with cleanup when a timed-out or cancelled caller leaves a physical invocation running
(decision 67, TASK-25, TASK-64, TASK-118). Production activation awaits those admission and host
qualifications.

Target config sketch: `image`, `container_name`, `labels`, `entrypoint`/`cmd`
template, `env`, `mounts`, `ports`, `gpu` (device requests / `count = -1` /
`capabilities = [["gpu"]]`), `stop_grace_ms`, `restart = "no"`, resource limits, and
`daemon = "unix:///var/run/docker.sock" | DOCKER_HOST`. The design allows a Docker-compatible
Podman socket at its own version floor. A native containerd adapter is out of scope
(different protocol, separate adapter).

### 5.4 Scheduler (pluggable; v1 policy included)

```csharp
public interface IRequestScheduler {
    // Chooses the next (request, engine) pair. Implementations must be
    // deterministic under the event log for testability.
    SchedulerDecision SelectNext(SchedulerView view);
}

public sealed record SchedulerView(
    ImmutableArray<QueuedRequest> Queue,      // priority-sorted, with metadata
    ImmutableArray<EngineRuntimeInfo> Engines // busy-ness, loaded models
);
```

**v1 policy (`PrefixAwareScheduler`)**, in priority order:

1. **Model affinity gate**: candidate engines must serve the requested model
   (loaded) or be able to load it if `autoSwitch` allows (see §7).
2. **Prefix affinity score**: each engine maintains a prefix trie over
   normalized message prefixes of recent requests (bounded depth/nodes).
   Matched-prefix length `P` (tokens or chars-normalized) yields an affinity
   bonus `A(P) = min(P, Pmax) / Pmax * AffinityWeight`.
3. **Busy-ness score**: `B = inflightTokens + α·queueDepth + β·estPrefillMiss`
   where `estPrefillMiss` = prompt length − matched prefix (the new tokens the
   engine must actually prefill, which stalls other requests).
4. **Decision**: engine with highest `A(P) − λ·B` wins; if all scores tie,
   least-loaded engine wins (round-robin tiebreak by engine name).
   Prefix affinity dominates only when `P ≥ PrefixAffinityThreshold`
   (config, default ≈ 512 normalized chars) — this implements "uncached
   requests go to the least busy server with the model loaded".
5. **Preemption**: none in v1 (engines we target don't support it); document
   as future hook (Llumnix-style migration is out of scope).

**Suffix reuse (forward design)**: `EngineCapabilities.SuffixCache` +
`PrefixAwareScheduler` gains a `SuffixAffinity` scorer once engines report
cached-suffix extents (an opaque `(hash, extent)` tuple). Scheduling
constraint: a suffix-reuse candidate engine is preferred only if the request's
suffix hash matches an engine-held extent AND the request is *not* streaming
to a different thread's affinity — details are deliberately unspecified until
engine forks land; the scheduler interface will not change shape.

### 5.5 Queue semantics

An enqueue commits its canonical model, queue position and ticket index from one current configuration
generation. An exact take checks the selected generation, engine identity and mapped queue identity
before it removes or charges the ticket. A stale selection ends the current dispatch pass before
selection commits. The next pass scans the current queues with the accepted canonical model.
The admission feed reports thread registration, when needed, before enqueue and reports
enqueue before selection or terminal settlement. A pending enqueue counts toward capacity. It cannot dispatch before its feed
publication, and later published tickets can use the queue while it waits. Cancellation and the original
monotonic deadline are checked at that publication boundary and again before exact take. Shutdown closes
both admission cuts before cancelling its wake token.

The offline verified binding revalidates the still-current exact preparation and route generation
before removing a ticket on a strict binding refusal. A stale result cannot settle its replacement.
Temporary capacity loss parks the ticket. A definitive refusal settles once without a queue timer.

- One global priority queue per canonical model, keyed case-insensitively. The default is
  immediate retirement of the exact mapped queue when its last member leaves. Pending
  enqueue publication counts as membership. Simultaneously populated queues follow normal
  admission with `max_queue_per_model` applying to each queue.
- Dispatch scans queues by increasing observed depth, then by the creation order of the
  live queues. A recreated queue goes after all existing queues of equal depth and preserves
  their relative order. Each scheduler call evaluates the first published ticket in that
  queue whose thread has capacity. Commit takes that exact ticket only while it is still
  first admissible. If the candidate is removed or displaced before commit, the next
  dispatch pass scans the current queues.
- **Offline exact-ticket admission.** The injected sender qualifies the queue handshake.
  Schedule outside the router lock. The host admission gate, router lock and model queue lock
  order the live check and exact removal. Validate the ranked route generation, model-bound
  window for an explicit minimum, free slots, thread room and original elapsed queue deadline.
  Preparation owns no physical slot. A preparing head preserves its queue order and another
  queue may progress. Each ticket may start one preparation per binding and route generation
  and have only one outstanding attempt, including retirement. The scoped preparation limit is
  the greater of two and the engine count at construction, shared by preparing, ready and
  retiring work. Cancellation, expiry, supersession and shutdown signal retirement. Work
  that ignores cancellation still occupies its preparation slot until it settles. A parked
  scan wakes on published state and checks its revision after arming. Its wait uses the
  captured shutdown token even if teardown has already disposed the source. Public `Start`
  schedules its loop without retaining the task. The offline fixture joins the loop it starts.
  Closing the route fences authorization. An authorized invocation counts before its first
  byte. Cancellation can release it early only by atomically preventing submission. The
  injected invocation owns the response through consumption and disposal, then finishes
  prepared-resource cleanup before releasing the permit. Each engine shares one physical
  budget across its routes and charges an ordinary use once while it occupies both the
  logical and physical counts. A cleanup fault leaves a submitted use counted after its
  logical request ends and emits `request.cleanup_failed` independently of the ticket result.
  An abort that claims an unstarted invocation prevents submission, and further
  abort calls wait on the same cleanup result. Execution that arrives after that abort waits
  for cleanup and ends as cancellation. The injected sender is under fixture
  control and does not exercise the production response timeout. An engine has at most one
  staged owner. An eligible owner has first opportunity for its final free permit when the
  owner's route opens in the same generation. Cancellation, expiry or failed preparation hands
  the slot to an ordinary joiner. The staged owner holds no counted use while closed.
  When a prior attempt or a full preparation table blocks an exact staged owner,
  a dispatcher wake reconsiders that request. An eligible owner waiting for capacity gets the
  first available preparation slot ahead of ordinary routes. An ordinary preparation rechecks
  that priority under the admission gate, including when a slot opens after the scan starts.
  Production admission still needs the enrolled host and verified transport. Production owner
  promotion and late cleanup remain TASK-25.
- **Engine-local send permission.** A historical switch result may still be delivered after
  engine shutdown. Entry to the upstream HTTP sender requires a separate claim under the
  transition gate that records the stop. If stop wins, the request settles as cancelled
  before reaching the sender. If the send claim wins, its local client remains owned through
  response disposal or a late send fault after a caller-facing timeout. An ordinary health
  round claims each GET under the same stop gate and stops when admission is refused, even
  if an earlier request keeps the client open. Cancellation of the poll loop bars a new
  round. An admitted round keeps its poll timeout and publication fences. Client disposal
  follows the last counted inference send or ordinary health GET. Switch readiness
  observations retain their separate deadline and cancellation rules. Shutdown still
  attempts lifecycle cleanup if HTTP close faults. Deferred cleanup cannot
  change a ticket's terminal response. Model-only forwarding uses the same check with
  an optional timing proof. Verified host admission and physical backend retirement
  retain their separate authority and evidence requirements.

- **Measured switch ordering.** The built-in scheduler can compare consecutive eligible switch candidates
  of equal queue depth and request priority on one configured native Docker engine. It needs an idle engine
  with a current proof for a single incumbent. The comparison ends at the first eligible route outside the
  group. The winner still uses the existing capacity, exact-ticket, lifecycle and readiness checks. A changed
  incumbent or displaced ticket restarts the scan. Custom schedulers keep baseline queue ordering.
  The estimate adds verified incumbent shutdown to the requested model and profile's activation. The estimator
  takes the median of up to five recent matching successes per phase and requires at least three samples no
  older than 24 hours. If any contender lacks a complete estimate, the group uses enqueue order. Equal estimates
  also use enqueue order. At a queue age of 30 seconds or more, the earliest enqueued eligible aged contender
  takes precedence within that group. At later eligible comparisons, only earlier enqueued aged contenders can
  precede it. This limits cost-induced overtakes within the group. Host admission, streams occupying slots,
  queue deadlines and batching determine the wider wait.
- **Switch attempts and evidence.** The runtime retains up to 64 terminal attempts in memory. Queue, ownership,
  preflight, shutdown, launch or delegated command, and readiness use monotonic observations. Attempt records
  distinguish absent, completed, failed and incomplete stages. A known zero is a completed observation.
  Native shutdown contributes only when exactly one proved owned container for the incumbent's model, profile
  and image is actually stopped and configured removal succeeds. The stop receipt must match both the
  incumbent's launch receipt and its sample identity. Already-stopped, absent, ambiguous and partial
  operations remain diagnostic. Native activation starts at launch and ends at confirmed readiness. It requires
  an acknowledged fresh container start. Fresh-container observations establish process freshness. Model asset
  digest and host cache state are currently unknown. Samples match the engine runtime, its bound configuration,
  canonical model, effective profile, image identity and observation class. After known eviction, a fresh
  launch uses matching fresh-container observations or has an unknown estimate. Delegated commands supply
  diagnostic command time. Their stop and load phases remain unobserved.
  Shutdown seals a cancelled attempt before releasing waiters. The first send attempt attaches once to the
  matching current proof immediately before the router calls the upstream HTTP client. The send can fail
  before reaching the engine. The timestamp supplies diagnostic timing only. Host drain, switch-rate
  bounds, verified responder identity and batching have their own work (§5.3.1 and §13.3). History
  expires on router restart.
- **Fairness**: within a class, `SubPriority` then FIFO; a `Weight` limits
  how many in-flight requests a thread may hold concurrently
  (`maxInflight = ceil(Weight)`), so heavy threads can't monopolize slots.
- **Overflow**: per-model queue cap → reject with `queue_overflow`, which maps to **429** in
  `ServerApp.MapStatus`, plus event `queue.rejected`. The code is pinned at library level by
  `EndToEndTests.Queue_Overflow_Fails_Ticket` and the body shape by `ErrorContractTests`; the 429 itself is
  asserted by no HTTP-level test. The `408` this bullet used to name is the queue *deadline*'s status
  (`deadline_exceeded`), which overflow is not.
- **Cancellation**: the caller's `CancellationToken` is wired to the ticket at enqueue, so a client
  that hangs up (an HTTP disconnect is `RequestAborted`) takes the same path as
  `DELETE /admin/tickets/{id}`: a queued request gives its place back, an in-flight request cancels the
  upstream engine call and releases the engine slot. Resulting code: `cancelled`.
- **One terminal state**: every settlement path claims the ticket's terminal transition atomically.
  A pre-queue ticket refusal publishes one `queue.rejected` with reason `queue_overflow` or
  `model_unavailable` and no `request.enqueued`. A ticket accepted into the queue publishes
  `request.enqueued` and settles with exactly one of `request.completed`, `request.failed` or
  `request.cancelled`. The first terminal claim wins, and any capacity reserved for the ticket is
  released once.
- **Queue deadline is a timer**: `max_queue_time` arms a timer when the request is enqueued. It bounds
  the *wait* only — after dispatch the deadline no longer applies. A dispatch-time check is not
  sufficient: a busy engine gives the dispatcher no reason to re-examine the queue. A ticket settled
  by this deadline emits `request.failed` with a reason sentence on the feed and has error code
  `deadline_exceeded`.
- **A model nothing here can serve is refused at admission and never joins a queue**: when every engine has
  answered its model probe, and none declares the model in its configured catalog, reports it loaded, or is
  switching toward it, the request is refused `model_unavailable` (503) **before** `TryEnqueue`. On the feed it
  is one `queue.rejected` carrying that reason, with **no** `request.enqueued` event: the ticket never occupied a
  queue and the feed does not pretend it did (§10). **Refusal beats the cap** — for a model no engine could
  ever take, "come back later" (429 `queue_overflow`) is the wrong answer, and refusing first is also what
  keeps that model's queue empty. The decision is taken against **one capability snapshot per engine** (health,
  reported models and the switch marker as a single versioned value, `EngineCapability`), and the scan repeats
  when any engine published during it: an unchanged sum of the per-engine versions across the pass is what
  proves the answer describes one state rather than a splice of two, and a scan that will not settle answers
  "not hopeless". Four edges, each a test: **unknown is not absent** (an engine that has never answered
  `/models` says nothing either way, so while one is unreached nothing is hopeless and the request waits);
  **a catalog entry is not the only way to serve** (a model the engine *reports* without any config naming it is
  eligible here exactly as in the scheduler, which shares the one predicate `EngineRuntime.CanServeModel` and now
  reads its loaded list, switch marker and eligibility bit from the same snapshot); **a switch in flight is not
  an absence** (a healthy engine switching toward the model keeps the request waiting — refusing there would kill
  the request that triggered the switch it is waiting for); **no engines is never hopeless** (config validation
  forbids zero engines, and `CapabilityScan` does not rely on that). An unresolved capability decision
  leaves the request subject to ordinary admission. If the configured queue deadline expires, it returns
  `deadline_exceeded` (408). A request admitted while capability was unknown can remain queued after
  absence becomes known. This admission check runs only before insertion.
- **Configured context refusal:** With the built-in scheduler, a positive explicit context minimum
  is refused before queueing when at least one engine declares the resolved canonical model, every
  declaring engine has an ordinal serving ladder for that key, and every tier is smaller than
  the minimum. The decision uses the configuration generation that commits the ticket. It returns
  `model_unavailable` (503 for a nonstream request), names the largest configured tier and the
  required tokens, and emits one `queue.rejected`. Equality or a larger tier leaves ordinary
  admission in place. A declaring engine without an applicable ladder can use model-bound serving
  evidence, so its presence also leaves ordinary admission in place. The custom scheduler admission
  path and estimate-based demand keep their existing behavior (§7.6).
- **What an engine claims about itself is current-and-verified or it is unknown.** The capability snapshot is
  evidence for two opposite decisions — route a request here, or refuse one with 503 — so a claim survives only
  while nothing has happened since it was made. Four rules, each a test in `CapabilityPublicationTests`.
  **Claiming a switch is a mutation:** the marker and `Healthy = false` go out together *before* the switch state is
  installed, because dispatch and the admission refusal read the snapshot and not the runtime's private claim; the
  reported list stays as history while the marker is up, and what is withdrawn is the entitlement to use it.
  **A probe is fenced, not merely ordered:** it captures the snapshot before it awaits and exchanges against that
  exact reference, abandoning the publication if it moved, so a fetch that began before a switch
  cannot land after it and overwrite what the switch proved — and, in the other direction, so a poll that **failed**
  before that switch cannot withdraw the proof afterwards. The negative answer withdraws health and model-window proof
  under the same version and the same drop rule, which is what stops one unreachable-tick poll from costing a working engine
  its model for a whole interval. Only the awaited `/models` fetch can supply an ordinary health observation.
  A fetch exception can withdraw probe health or contradict a matching incumbent proof. The loop token stops
  future ticks. An in-flight fetch keeps its own timeout and publication fences, so a failed response can
  still withdraw the proof of its own generation. Cancellation attributable to the loop token, timeout setup,
  diagnostics, event delivery, serving-state and readiness work, gauges and timer faults carry no such authority.
  After `/models` succeeds, the ordinary round reads the configured serving-state endpoint. It publishes the
  roster, discovery, health and serving capacity together. Missing or malformed state yields unknown capacity
  for the unavailable values while the roster remains healthy. A failed `health.expect` can coexist with valid
  gauges. The rule determines whether a **switch** counts as ready. `engine.health` reports the completed poll
  after the publication decision, including a stale round. Runtime shutdown suppresses late poll telemetry.
  A successful switch reports its profile and nudges dispatch after clearing the marker. The fence
  orders *publications*, not the engine: an engine restarted outside the router's sight, with no visible
  discontinuity, evades it. Ordinary polls publish neither a positive nor a negative while the switch marker is set.
  The immutable model-window observations used for discovery (§6) are published with the same roster and version.
  A newer observation replaces them in full, and a failed model probe, claim, or runtime shutdown withdraws them.
  **Uncertainty is published:** a switch that ends without having verified the engine — failed stop, failed launch,
  readiness timeout, an exec with `wait_health = false` whose refresh cannot answer — publishes `Healthy = false` as a
  health-only change that preserves the currently reported list, so the refusal says "not hopeless" and no stale list can
  refuse a model that has begun to be served or vouch for one that was just stopped. After a failed mutation,
  this publication clears `HealthFromClaim` and advances the version while the marker remains raised, even
  when health was already false under the claim. Clearing the marker is a separate versioned publication.
  An unverified switch ends **not-ready** and emits `engine.switch_failed`, never a completion, because an exit code is
  what the router was told and not an observation it made — the owner's posture (decision-27), which replaces the earlier
  promise that a `wait_health = false` exec was served on its own word. A switch that *does* verify publishes the reported
  list and `Healthy = true` in one publication, which is what makes the engine eligible without another poll.
  **A proof has to name what it proves:** on the `wait_health = false` path the post-launch `/models` refresh is the
  only evidence the router has, and a list that does not contain the requested model **refuses the switch** —
  `engine.switch_failed` with reason `refresh_omits_<model>` carrying what the engine actually reported, `Healthy = false`,
  and a not-ready result — and the check runs **before** any proof is published, so a refused switch leaves nothing
  presented as verified and burns no version. Trusting the exec skips the readiness *rule*, never the check that the
  engine says it holds the model. The names an engine reports and the catalog's model keys are **one namespace**, matched
  `OrdinalIgnoreCase`; a catalog **alias** is the router's request-side spelling and never satisfies that check, so an
  engine that reports only an alias is refused and the fix is the catalog key — the same namespace `IsReadyFor` already
  assumes, and nothing here translates engine names into config names. A refresh that cannot answer at all refuses the
  switch the same way, with `engine.switch_failed reason=refresh_unanswered_<model>`: an exit code is not a loaded model,
  and the rule that a proof has to name what it proves does not bend for an engine that will not say. What either refusal
  **quotes is bounded in length as well as in name count** — at most eight reported names, each keeping its first 24
  characters with its true length and a fingerprint marked, and always the count of what was left out — because the event ring caps entries rather than characters
  and the names are chosen by the engine, not by the router.
  **What the caller is told is a category, not an account.** A switch attempt that **ended** without readiness records
  its failure as one token from a closed set with one owner in the source — `SwitchReason.All`, in `WireText.cs`:
  `no_select_command`, `launch_failed`, `health_timeout`, `refresh_omits`, `refresh_unanswered`, `window_too_small`,
  `switch_error`, `no_lifecycle_owner` — and that token, and nothing the attempt measured, is what the caller's
  failure carries: no sentence wrapped around it, because a caller branches on a name and not on prose it has to
  parse. **The set is a door, not a comment.** A failed answer is built either by naming a member at the call site or
  by handing over what the attempt recorded, and both doors reject a name outside the array, so an exit that invents a
  phrase fails at the answer instead of reaching a caller as a sentence. The test parses `EngineRuntime.cs` and
  checks factory calls, recorded-reason writes, and failed-result construction. It rejects `with` initializers
  that write `Detail` or `Outcome` throughout that file, including clones of unrelated records. This syntax check
  has no symbol or data-flow analysis. The runtime tests exercise the refusal exits. Each name is
  exactly one caller action — **fix the configuration** (`no_select_command`, `refresh_omits`, `window_too_small`),
  **retry later** (`launch_failed`, `health_timeout`, `refresh_unanswered`), **retry** (`switch_error`), **retry
  elsewhere** (`no_lifecycle_owner`) — and §6.1 carries the table an integrator reads. The measurements stay on the
  feed because they are what an operator acts on: a
  provider's exit status and action line, the readiness window in milliseconds, the names the engine reported, both
  sides of a window comparison. A caller's **own** identifiers may come back to them — the model they asked for, the
  profile they named, the engine that refused, their own ceiling in milliseconds — because those are already theirs.
  Another request's identifiers never do: the ceiling sentence and the shutdown sentence used to name the model the
  in-flight switch was loading, which put one caller's request into another caller's error body. Engine shutdown
  returns the typed switch outcome `RouterStopped`. The request path maps it to the shipped `cancelled` (499) response
  without naming a model. A caller's own cancellation token still cancels its wait.
  Explicit admin switches acknowledge asynchronously and publish each caller's own waiter result
  under its operation ID (§6). Use the structured reason for the action above. Two explicit requests
  can share an engine attempt and still each have an ID and a result. Queue-driven switches require
  no admin operation and consume no operation slots.
  Exception text — a failed attempt's, and a health probe's own on `engine.health` — is bounded where it is written (§10).
  **An unbound engine refuses a switch before claiming it.** With external ownership, such a request
  answers `no_lifecycle_owner` and leaves the last proof published. A healthy external engine remains
  a routing candidate. Its probes still publish proofs and `IsReadyFor` still answers from them.
  A switch task that never started — a synchronous failure at the scheduling
  boundary, a thread-pool rejection — answers `switch_error`, the catch-all, because the caller's action is the same
  as for an attempt that threw; what separates the two is the **phase**, `switch_not_started`, which is feed-only and
  deliberately outside the caller's set: a caller shown a phase is a mapping bug, and the closed-set walk is what says
  so. That record is written **before** the switch is ended, and the end sits in a `finally`, because the ring calls
  every subscriber inside the append with no isolation (§10, TASK-103) — an observer that throws must not be able to
  leave the engine latched mid-switch, and no waiter may wake onto a refusal whose explanation is not on the feed yet.
  An **expired wait** is the third case and carries **no** name from the set: the attempt it was parked behind is
  still running, so a set that says why an attempt *stopped* does not reach it, and the caller is quoted their own
  ceiling and the engine's state and never the other request's target. The request-side wait ceiling of §5.4 sits
  inside that scoping; it is not a ninth name.
  **Post-launch readiness has one monotonic deadline.** On the non-enrolled switch path,
  the provider's readiness timeout starts after a successful launch. It covers the complete
  models and state observations, including response bodies and parsing, any poll delay, and the
  guarded proof decision. At equality the attempt is expired. A zero or negative readiness
  timeout expires before the first read, including `-1`. The delegated selector's process-exit wait
  and the request's own switch wait keep their existing meanings. With `wait_health = false`,
  readiness requires a fresh roster naming the target. A selected tier or positive explicit context
  minimum also needs a fresh bound window. If the shorter state cap expires first, the attempt refuses
  `window_too_small` while late I/O retires. At the guard, stop or absolute expiry can
  supersede an earlier step-cap result until the terminal outcome commits. A fresh attempt
  may observe independently while the retired read disposes. At overall expiry, the guard keeps the
  last completed in-budget diagnosis, including an omission, failed model-list refresh, or measured
  window mismatch on the trust path. Evidence first returned at or after the deadline cannot supply
  the diagnosis. Late reads cannot publish proof, change serving capacity in either attempt, or
  change the terminal outcome.
  The readiness mapping defaults to **health_timeout** for `NeverHealthy` and any other miss kind
  without an explicit arm. An attempt with a missing or empty recorded refusal reason defaults
  to **switch_error** when the router builds the caller's answer.
  A stop under the claim guard wins while readiness is pending.
  An already committed readiness outcome is delivered to its own joined waiter after the outcome
  event, even if engine shutdown or the waiter's timeout wakes it first. Caller cancellation
  can still leave that wait, and shutdown closes dispatch.
  **The readiness window names what its last poll saw.** A `wait_health = true` switch ends unsatisfied three ways,
  and they are three refusals rather than one timeout: an engine that never answered the probe is `health_timeout`
  (**retry later** — it may answer next time); an engine that stayed up and never reported the target is
  `refresh_omits` (**fix the configuration** — the catalog key has to match what the engine reports, and an alias is
  the router's spelling, not the engine's); an engine serving the target below the required context window
  is `window_too_small` (**fix the configuration** — supply a window that fits the request). Under v0.12.41
  all three were `health_timeout`: a permanent mismatch and a transient wait wearing one name, which is the exact
  distinction a retry decision exists to make. The expired window and its milliseconds stay on the feed at all three
  exits beside the names the engine reported and both sides of the comparison, so the grep key for an expired
  readiness window survives the name change. `wait_health = false` reaches the same names from its own evidence, and
  the prospective claim first checks the bound owner and then resolves the target against the startup
  catalog. A managed engine with an absent entry, or a delegated engine with a missing `select` or blank
  `exec`, answers `no_select_command` with no marker or capability publication. The transaction receives
  that same engine and target definition. Changing a source dictionary or a same-name config definition
  cannot change what a runtime is authorized to switch. A new runtime uses the new configuration.
  **Target validation is skipped for a caller that has come to join** (decision-51). Under the transition gate the
  order is fixed: read the switch state, and a switch already running on this engine is **joined** — its completion
  awaited with no catalog read at all. Only a caller the idle engine hands to the claim path is asked what its catalog
  says, and an invalid answer is a **terminal refusal for that attempt**: no marker, no state write, one feed record,
  selected under the gate and emitted after the switch lock is released, so no feed subscriber runs holding an engine
  lock. The guard used to sit ahead of the whole loop, and that was not a detail: configuration state that never
  governed the running switch answered a caller that had arrived to wait behind it, and because a refusal is terminal
  that caller never learned what the switch decided. Measured on the pre-fix tree, a caller joining a switch that had
  been claimed on a usable entry, whose entry was then replaced, was answered `no_select_command` — **fix the
  configuration** — for a transaction that was running, and when that switch failed it was never told `launch_failed`
  — **retry later**, the action it should have been given. A caller that joins another request's switch and is
  overtaken by its failure takes its own turn through the claim path, and <b>there</b> its own absent entry refuses it:
  that refusal is correct and is asserted as correct, not special-cased away. And a budget that expires while a caller
  is joined is a **timeout**, never a refusal — one absolute deadline, set before the first lock wait, never reset
  across a wait, a join or a re-loop. Four pins, all four red before the change and green after it. **What the set names is what a completed attempt observed**, not a diagnosis of
  the engine: a last poll that saw a healthy engine omit the target may precede a model still loading, a window gauge
  can be old, and a refresh that cannot be read at all during a `wait_health = true` wait ends `switch_error` rather
  than one of the three readiness names, because the attempt itself threw. A timed-out wait says nothing about the
  attempt at all and carries no name.
  An ownership or preflight refusal verified before any possible mutation clears its matching claim
  and restores the displaced incumbent's health and per-model discovery windows together. The claim must
  have displaced a healthy proof and still own the same version. A poll started on that claim can veto
  the full restoration if its models query fails or its roster or discovery windows differ from the incumbent.
  If that contradiction arrives after refusal, it makes the exact restored snapshot unknown, even
  after another poll from the same claim has withdrawn its serving tuple.
  When the model observation agrees, a missing, unavailable or different serving observation invalidates
  only the whole serving tuple. An identical tuple remains valid when `health.expect` alone fails. Marker
  clear restores either the entire still-valid tuple or unknown serving capacity alongside the roster proof.
  A matching late serving-only contradiction clears the restored tuple in one versioned publication.
  Model-only readiness remains available, and a waiter that needs the invalidated window cannot use it.
  Restoring the still-valid serving tuple preserves its switch timing proof. Withdrawing the serving tuple
  or the whole capability proof also retires the timing proof.
  Each committed withdrawal nudges dispatch. Later observations from that claim cannot restore withdrawn
  proof. The poll publishes no capability through the marker or across an independent publication or
  successor snapshot. An observation started before the claim cannot revoke the restored proof.
  Attempted stop or launch, a thrown transaction, a superseded claim and shutdown prevent restoration.
  The caller still receives the switch failure and its existing category, even when the incumbent remains schedulable.
  A quiet poll keeps the published roster and version when the roster and context windows agree with the
  current snapshot. Without window evidence the ordered roster comparison ignores case. With window
  evidence it keeps the reported spelling alongside the proof. A reordered roster, a repeated name that
  changes the roster, or a changed window advances the version.
  The two ends of a transition are ordered by one await-free gate (taken after the switch lock, never the other way
  round, since the switch lock's holder calls the end): the claim publishes marker-then-state inside it, the
  switch-state read that decides claim-or-coalesce happens inside it, and the end writes idle state and clears its
  marker inside it only while the marker still names *that* switch — a late end from a finished attempt must not hide
  a running load by erasing a successor's marker. `IsReadyFor` requires a healthy, marker-free snapshot that contains
  the target with a fitting window; the reported list and the window gauge alone no longer answer readiness. The
  commit of a transition signals the dispatcher directly (no new event kind, no wire surface changes): the switch
  outcome events are emitted *before* the commit, so a dispatcher woken by them sees the marker still up and would
  otherwise not look at the queue again until the next health poll.
- **The claim is decided and published from one snapshot, and a switch's end settles before it nudges.** The
  readiness answer and the marker use the same capability snapshot. An idle offer captures it before
  acquiring the switch and transition locks. If the compare-exchange loses or the idle answer is stale,
  release both locks before re-deciding. A poll that proves the requested model can retire the offer.
  After eight lost idle offers, return `switch_failed` with `switch_error` without starting a lifecycle
  operation. The caller's original finite budget bounds every acquisition of the switch lock
  and the subsequent decision to join an active claim or start an idle one. An immediately ready caller can
  still use its current proof, and an already joined caller retains its committed outcome under the
  delivery rules above. Caller cancellation and shutdown retain precedence.
  **A waiter is authorised at the moment it resumes, not at the moment it was promised**. It uses the
  evidence this router has about the engine at that point. The readiness task settling and the switch state
  clearing happen before a waiter is scheduled, so a successor can claim the engine in that window. A claim is one
  publication that takes health down, raises the marker, and says nothing at all about what the engine serves: it is
  somebody else's transaction starting. Withdrawing a proof this router published **itself** on that snapshot is
  wrong in both directions — answering the waiter `Failed` fails a request over a switch that worked, and sending it
  back through the claim path buys a lifecycle transaction that unloads the model it is waiting for and drives the
  successor into the same fall-through, so two callers wanting different models on a one-model engine alternate
  switches until one runs out of ceiling. At resume there are four answers, in this order: the current snapshot
  authorises the target, so `Ready`; the target is still stood behind by the proof **this caller's own switch
  published** — the model still in the reported list, a window that still fits, and health absent only because the
  claim that raised the marker is what took it down — so `Ready` as well; this caller's **own** attempt ended false, so `Failed` once, with the recorded
  reason category — nothing retries inside the engine, and a caller with no ceiling would otherwise relaunch a switch
  that cannot get better; anything else takes its own turn through the claim path, exactly as single-flight fairness
  prescribes for a caller overtaken by another target, bounded by its own ceiling and never by a refusal. The
  anything-else is a contradiction or a stranger: the target gone from what the engine reports, a window that no
  longer fits, health withdrawn while no switch is in flight (which is a probe, not a claim), or a switch aimed at
  another target or another serving profile. **Which of the two took health down is a fact the snapshot carries, not a
  fact a reader infers.** One boolean answers "do we know" and cannot also answer "who made us not know", so the
  snapshot carries a second, internal bit: the claim that raised the current marker authored the withdrawal, and it is
  cleared by every other author — a positive probe, a switch's own proof, a probe negative that was allowed to publish,
  a switch exit, a marker clear — and is never set by a claim that started from an engine already unknown. A claim over
  a healthy engine and a probe negative publish the same `Healthy = false`; only one of them is an observation, and the
  bit is what keeps the resume rule from reading the other as one. An exit that finds health already false still
  publishes when the claim in flight was the author, because the provenance changed: the version moves on a
  provenance-only publication, or a scan cannot tell "the claim withdrew health" from "the transaction made the engine
  unknown". What this does **not** buy is authority held across the admission
  handoff: a waiter answered `Ready` off its own proof can still have that model unloaded by the successor's
  transaction before its request reaches the engine. That half is unload authority, and one sentence names its
  owners: a request authorised at admission must not lose that authority between the wait returning `Ready` and the
  engine receiving it (**TASK-25 AC #5**), and a ticket approved by an earlier scheduler pass must not be dispatched
  to an engine claimed after that pass (**TASK-64 AC #3**), with whose-switch-runs policy in the age-weighted drain
  (**TASK-72**). It is recorded as an accepted gap in §15 rather than closed here by a fall-through that ping-pongs.
  Inside the transition gate the
  state write still follows the marker. **Settle, then nudge:** the readiness task is completed first, then the
  dispatcher nudge runs with the gate released and its own failure swallowed. A callback that throws must not
  rewrite a switch that already committed — before, the throw escaped into the switch's own catch, turned a proven
  switch into a failed one, and left the waiter parked until that catch ran — and a lost nudge costs at most one
  poll interval, not the switch. Two failure edges settle the same way: a synchronous failure at the scheduling
  boundary undoes its claim (settle, clear the marker, engine claimable again), and so does a claim hook that
  throws, with the undo taken **after** leaving the gate because the gate is reentrant and undoing inside it would
  run the settle and the nudge while every later claim queues behind it. **The first published switch outcome wins.**
  Shutdown latches once under the same transition gate that admits a claim or commits its outcome. The gate checks
  the latch before joining or claiming a switch. Shutdown records `RouterStopped` for the installed attempt under that
  gate, then settles readiness and cancels the engine's switch token after releasing the gate. The linked tokens
  release callers waiting for the switch lock as well as for readiness. Either wake-up yields the same recorded
  switch outcome. Shutdown also publishes a newer capability snapshot with health, health provenance,
  windows, serving capacity and proof withdrawn. It retains the last reported model roster as history.
  An idle engine stops answering model-only readiness as soon as this snapshot is published.
  The transaction keeps its marker until it unwinds and clears the marker itself. If readiness or failure committed first,
  that outcome remains available to its waiter under the ordinary resume rules. At expiry the same gate answers from
  those rules first. A caller that would still need another claim then receives `RouterStopped` if shutdown has
  latched, even if the foreign attempt it waited on has already finished. The public switching flag is derived
  from the same capability marker used by the scheduler. The runtime keeps one switch token for its lifetime.
  `ShutdownStopCauseTests` parks a caller past its initial stop check to test the gate, holds a
  waiter across a late provider failure to test the frozen cause, and exercises both orderings of readiness and
  shutdown. The claim edges are pinned in `SwitchClaimTests`; the retired claim, in
  `CapabilityPublicationTests`. The sequence the withdrawn rule broke is pinned there as well: two callers wanting
  different models on one engine take **one lifecycle transaction each, in arrival order**, asserted as a launch
  sequence while the successor's load is still held (`TwoCallersWantingDifferentModelsTakeOneSwitchEach`).
- **The other two waits are bounded separately**: a model switch and an engine's turn each get their own
  ceiling, because an expired queue deadline says nothing about them. `switch_wait_timeout_ms`
  (scheduler, default `0` = no ceiling) caps how long a queued request waits for a switch it triggered:
  past the ceiling that request fails `switch_failed` and its reserved slot is handed back, while the
  switch itself runs on under its command's own `wait_timeout_ms` — the ceiling is about the caller, not
  about the engine's state machine. That separation is ownership, not convention: the switch runs on a
  token **the engine owns**, never the initiating request's, so a waiter that gives up neither stops the
  switch it was waiting for nor latches a failure the other waiters did not cause
  (`ARequestThatGivesUpOnASwitchDoesNotStopTheSwitchItWasWaitingFor`). Switch waiting stays single-flight
  per engine and fair: a waiter whose target differs from the switch in flight is not judged by that
  switch's outcome, it takes its own turn, and every failure path — including `no_select_command` —
  resets the switch state, so one failed selector cannot wedge an engine
  (`AMissingSelectCommandFailsThatRequestWithoutWedgingTheEngine`). A target is a model **and** a serving
  profile: a waiter asking for `m @ 32k` is never answered by a switch to `m @ 8k`, ready or failed
  (`SwitchFairnessTests`). A ceiling expiry and an instant
  selector failure are worded differently: the first says the switch is still running, the second says the
  selector could not serve that model.
  Per-engine `response_timeout_ms` (default `600000`, `0` disables) caps how long an engine may sit on a
  request it already accepted. For a stream the ceiling ends at the **first positive raw read**,
  which also records `request.streaming_first_token`. When armed, the gate covers the full
  successful non-streaming body or the capped excerpt from an upstream non-2xx response (§10).
  Headers followed by silence time out
  (`HeadersWithoutABodyAreNotAnAnswerSoTheCeilingStillEndsTheRequest`). A stream whose engine sends
  no bytes before the ceiling fails **in band** with one `engine_timeout` error frame and `[DONE]` on a response whose
  `200` was already committed — because there is no status left to change (§6, and
  `AStreamThatNeverSendsAFrameEndsInsideTheStreamBecauseTheStatusIsAlreadySent`). The ceiling loses a race
  cleanly: if the owed bytes are already in hand when it expires the caller gets the answer rather than a
  timeout, and the router does not throw away a completion that arrived a millisecond late (§15). The
  ceiling never rides
  the caller's cancellation token: the response gate has its own source, and when the ceiling wins the
  router still asks whether the caller hung up first, so an abort reports `cancelled` even against an
  upstream handler that ignores cancellation
  (`ACallerThatHangsUpIsCancelledEvenIfTheEngineIgnoresTheCancellation`). A ticket's terminal transition
  owns closing its stream channel — the read pump does not close it — which is what lets a stream whose
  engine sent headers and no frame end with an error frame instead of stopping silently. A queue deadline
  that expires while a request is *waiting for a switch* is not a failure either: the wait continues and
  the request is dispatched when capacity and health arrive (regression:
  `WaitForSwitchAndDispatchAsync` used to drop the ticket).
- **Dispatch is level-triggered on engine state**: the dispatcher wakes on arrival, on capacity
  release, and on `engine.health` / `engine.profile` / `engine.switch_*`. A request that arrived before
  the first health probe is dispatched when health arrives, not when the next request arrives.
- **Shutdown**: in-flight work is cancelled and every still-queued ticket is given a terminal
  `cancelled` state, so open relays end rather than waiting for the process to exit.
- **Ticket retention**: finished tickets stay answerable on the admin surface for the most recent 4096
  (the event ring's capacity); older ones are evicted so the ticket table cannot grow with traffic.

## 6. HTTP surface (server project)

OpenAI-compatible (path-for-verbatim):

- `POST /v1/chat/completions` — body `chat/completions` DTO; `stream: true`
  streams complete SSE blocks verbatim. `StreamFrameIdentityTests` compares the client's bytes with the upstream's
  at every split of a short fixture and all 64 alignments of a longer fixture.
- `POST /v1/completions` — same for legacy completions.
- `GET /v1/models` returns the OpenAI model list envelope with the last reported loaded row for
  each model and engine, in roster order. Entries retain `id`, `object`, `created`, and `owned_by`.
  A healthy engine with no switch in flight may also report `context_window` as a positive
  signed 64-bit JSON integer in tokens. It comes from the same observed loaded row and immutable capability snapshot
  as the ID. Queries do not change this contract. Rows with the same ID on different engines each
  describe their own route candidate. A row with unknown or ambiguous window evidence omits the
  member. This includes aliases and repeated case-equivalent IDs anywhere in an engine's
  observation, including rows filtered out of the loaded roster. A row also omits the member if
  either request canonicalization or forwarding on that engine would select a different model.
  A historical row can remain listed after its proof has
  been withdrawn. The response uses `Cache-Control: no-store`. A window describes the last
  accepted observation for the named engine and grants no admission or engine reservation.
- Errors: engine errors proxied with status preserved; router errors use
  OpenAI error JSON shape with `code` values: `model_unavailable`,
  `engine_unreachable`, `queue_overflow`, `deadline_exceeded`,
  `switch_failed`, `cancelled`.

Admin/control (not OpenAI-compatible, our own contract):

- `POST /admin/threads` register/update thread (priority API over HTTP):
  body `{ threadId, priority: {class, sub}, weight?, modelAffinity? }`.
- `POST /admin/switch` — request model switch: `{engine, model, profile?}`;
  refuses if a switch is in flight unless `force`. `profile` names a declared
  serving tier; the ack echoes it and the wait-for-ready loop requires the
  reported window to fit. An accepted request returns 202 with `Engine`, `Model`, `Profile`,
  `Switching: true` and `OperationId`, a server-generated ID of 32 lowercase hex characters.
  Save the ID for lookup. Acceptance starts a detached waiter that continues if the POST disconnects.
- `GET /admin/switch/{operationId}` returns 200, `Cache-Control: no-store` and the PascalCase
  object `{"OperationId":"…","Status":"…","Reason":…}` for a known operation. Status is
  `pending`, `succeeded`, `failed` or `cancelled`. A success means the target was ready for that
  waiter when it answered, including an already-ready target. The operation is protected during
  the wait and the server ACK send. On the same running router it remains queryable for 15 minutes
  from the later of terminal settlement and server-side ACK completion or send termination.
  Reads do not extend retention. Malformed, unknown and expired IDs return 404 `not_found` with
  `Cache-Control: no-store`. This includes old IDs after restart or on another router instance.
  The ACK send has one 30-second deadline to initiate abort if its write or completion is still
  pending. The send stays owned until it retires. Remote receipt is outside this deadline.
  The router reserves space before accepting a POST, with at most 32 pending and 288 total
  operations. When either limit prevents admission, it returns retryable 503
  `too_many_switch_operations` before starting the waiter. Only expired records can be reclaimed.
- `GET /admin/config/reload` reads the current configuration generation. `POST /admin/config/reload`
  applies the startup file to that generation (§7.7). Both routes require the startup reload bearer
  credential. Both return 200, `Cache-Control: no-store`, a strong `ETag` and a PascalCase
  `{"Generation":n}` body. POST accepts one current strong `If-Match` tag and requires bodyless
  HTTP framing. Chunked or other body-capable framing is refused even if it carries zero decoded bytes.
  Tags are bound to the serving instance and generation. The first generation is 1. A no-op keeps
  the tag. Failures use the lowercase error shape in §6.1 and omit the tag.

  | HTTP | Error code |
  | --- | --- |
  | 400 | `invalid_reload_request` for a missing or malformed tag or a request body |
  | 401 | `reload_unauthorized` with a Bearer challenge |
  | 409 | `reload_busy` when work or the previous topology handoff prevents apply |
  | 412 | `reload_stale` for another serving instance or an earlier generation |
  | 422 | `reload_invalid_config` for a read or validation failure |
  | 422 | `reload_requires_restart` for changes outside §7.7 |
  | 503 | `reload_unavailable` once the router starts closing |
- `POST /admin/config/target-reset` requires a file-backed startup with the reload Bearer and
  a separate reset opt-in (§7.7). Send one current reload ETag in `If-Match` and JSON
  `{"engine":"…","model":"…","profile":"…"}` naming the engine, canonical model and exact
  effective profile. Use an explicit empty string for an unprofiled target. Success returns
  204 and keeps the generation. A malformed request returns 400 `invalid_target_reset`.
  An unknown, unlatched or busy target returns 409 `target_reset_unavailable`. Authentication,
  staleness and shutdown use the reload error envelope and codes above.
- `GET /admin/catalog` reads one applied configuration. The response contains `Instance`,
  `Generation`, `DefaultModel` and `Engines`. Each engine names its `Name` and configured
  `Models`, including cold entries. Each model carries `Id`, `Aliases`, nullable
  `DefaultProfile` and `Profiles` with `Name`, `MaxContext` and `MaxSlots`. A matching
  `If-None-Match` yields 304 with an empty body. The ETag changes on restart or a new
  applied generation. Each changed publication emits `catalog.changed` as an invalidation
  hint. Clients re-read the catalog after that event and when reconnecting.
- `GET /admin/status` — current engine health, loaded models, switch target, serving profile, context
  window, slots and in-flight requests. Also reports depths for populated model queues, including
  members awaiting enqueue publication. A retired queue's model key is omitted (§5.5). The
  response includes thread priority, weight and enqueue/completion counters, and
  `FeedSubscriberFaults`, the number of observer invocations that have thrown since startup (§6.1).
- `GET /admin/events?since=<seq>` — SSE feed of `RouterEvent`s (see §10),
  `text/event-stream`, replayable from ring buffer by sequence. Optional filters:
  `kinds=a,b` (exact kinds), `thread=<id>`, `engine=<name>`, `ticket=<id>`.
- `GET /admin/tickets/{id}`, `DELETE /admin/tickets/{id}` (cancel).
- `GET /admin/tickets/{id}/events?since=<seq>` — the same feed restricted to one ticket: the
  replayed history and the live tail are both filtered, so an agent following its own request
  never sees another session's traffic. Headers flush before the first event, so an unknown
  ticket id is a quiet 200 stream, not a hang.
- `POST /admin/queue` — future listener/host enqueue path (spec'd but
  hostless in v1; returns 501 until §12.1 ships).

Admin JSON naming is currently **PascalCase** (the source-gen context's default;
tests assert `"ThreadId"`, `"State"`, `"TicketId"`, and `EventSurfaceTests` asserts the event
feed's `TicketId`/`Kind` casing too). §10 and `samples/pi/inference-router-tools.json` describe
these fields in lowerCamel, which is the shape an OpenAI-style caller would expect — unifying the
admin surface on lowerCamel is an open decision (§15.1), not a side effect of other work. That is a live wire contract,
so the casing must be pinned deliberately before the Pi extension and the Blazor
client start parsing it (§15 open question).

**Streaming handoff**: the SSE relay does not poll for the ticket's stream channel. The
channel is created at admission, before the `Executing` event is published, and
`RouterTicket.StreamReady` resolves with it (or with `null` at a terminal state). The relay flushes
one initial keepalive and then one every 4 s until the first complete upstream SSE block or a terminal
result. A complete upstream comment-only block counts. Installing the channel carries forward the same
monotonic deadline and sequence. Available complete bytes or a terminal result takes precedence over
a due keepalive. The relay emits no more keepalives after it starts forwarding upstream bytes.
Each keepalive is an SSE comment such as `: [router keepalive seq=7 status="switching model=qwen-long engine=hyperqwen"]` followed by a blank line.
The sequence starts at zero and increases by one on that stream. The optional JSON-quoted `status`
appears on the first comment and when the reported status changes. Model and engine identifiers use
the 128 UTF-16 code-unit reporting limit (§10). A missing engine name renders as empty text. Each
encoded keepalive is at most **1,727 bytes**, including both terminating line feeds. The longest
status has 24 fixed code units plus two reported identifiers. Allow up to six bytes per status code
unit after JSON escaping, ten decimal sequence digits, and 37 bytes for the frame text and quotes.
SSE parsers ignore comments when dispatching data events.

The pump holds one unfinished block until its blank line, preserving LF, CRLF and bare CR across
upstream read boundaries. The per-block limit is 1 MiB including all bytes on nonblank lines and their
line endings. The blank delimiter contributes one or two bytes outside that limit. The read scratch is
16 KiB and one validated batch can contain up to 1 MiB plus 16 KiB. Growing the staging array
temporarily retains its old allocation during the copy.

EOF with an unfinished block and a block over the size limit both fail with `engine_error`. This
includes an otherwise valid oversized event and an upstream `[DONE]` line without its blank delimiter.
A read cancelled by the upstream alone uses the same block-boundary check as EOF. An I/O fault
such as an upstream disconnect uses `engine_unreachable`. The relay discards the unfinished
block, preserves the complete prefix, and sends a standalone `data: {"error":{…}}` event
followed by `data: [DONE]` before closing. Clean EOF at a block boundary adds no bytes, including on an
empty upstream or after complete blocks without a sentinel. A caller disconnect closes the relay
without an extra terminal frame. The benchmark harness measured the time-to-first-token delay from
the previous poll-based handoff (§14, `docs/benchmarks.md`).

**Core shutdown during a default active read.** After publishing a complete upstream block, the
producer arms a subsequent raw read only when it returns an incomplete operation and the linked,
shutdown, execution and caller tokens are still live. The linked-token callback can record the
cancellation source. The producer observes the read result and retires the arm. Their order is
determined by those logical observations, independent of when the upstream physically completes
the read. Cancellation before arming is ineligible. A positive result qualifies only after the arm has observed core shutdown without
execution or caller cancellation. An `OperationCanceledException` carrying the read's actual
cancelled linked token can also qualify, including when that result observation itself first
establishes the same core-only source. Qualifying late bytes are discarded. A committed HTTP 200 with a live caller and a locally
acknowledged complete data prefix receives the existing lowercase `cancelled` error event followed
by one `[DONE]`. The writer skips the terminal if any write or flush had uncertain progress or if
the acknowledged prefix already includes an upstream sentinel. Local acknowledgement describes
server write progress. The ticket and feed record cancellation once. This behavior applies with
both HTTP SSE limits disabled and starts after the initial raw read and stream acquisition.
Queued requests, model waits, opt-in SSE and library streams follow their existing rules.

**Opt-in HTTP SSE limits.** A positive `stream_buffer_limit_bytes` caps outstanding output payload
per stream, including queued bytes and the bytes still owned by a pending flush. The pump transfers
one validated batch in order using credit-sized slices and pauses upstream reads at exhausted credit.
The staging buffer, read scratch, validated batch and transport buffers have separate ownership.
A continuously pending downstream write times out at 30 s without progress, even with one byte
pending. The router aborts that HTTP exchange and records ticket code `slow_consumer` and a
`request.failed` reason beginning with that token. An abnormal writer exit settles the exchange
as `cancelled` unless another outcome already won. A pending write keeps its own reserved credit
until the operation settles, even after the exchange ends and unrelated queued output is discarded.
A successful flush restores credit. On clean
upstream EOF, the router disarms the response timer and starts transport cleanup. The HTTP ticket
succeeds after the last required successful flush, including the initial comment on an empty body
and any upstream `[DONE]`. The first selected terminal outcome stands. Caller or shutdown
cancellation observed before a watchdog or final acknowledgment claims the outcome is recorded
as cancellation. A later cancellation leaves an already accepted final acknowledgment intact.
Kestrel's flush acknowledgment describes relay progress.

A positive per-engine `stream_idle_timeout_ms` starts a separate deadline on a pending raw read
after the first positive read. Time waiting for output credit and HTTP keepalives do not run or
reset this deadline. When upstream idle wins, the router cancels upstream, discards the unfinished
tail and sends the existing in-band `engine_timeout` error and `[DONE]` after the complete prefix on
a writable exchange. If that prefix includes an upstream sentinel, the idle outcome remains on the
ticket and feed and no second terminal is appended. The logical slot is released once and physical
transport ownership lasts through actual cleanup under §5.5. These settings apply independently to
HTTP SSE. Direct library callers retain producer-side stream completion and may drain after
awaiting `CompletionTask`.

**Threading through HTTP**: clients convey thread + priority via headers:
`X-Inference-Thread: agent-42`, `X-Inference-Priority: high:200`
(`class[:sub]`), `X-Inference-Affinity: <normalized-prefix-hash>` (optional,
server-side prefix normalization is authoritative anyway (§7.6),
`X-Inference-Profile: <name>` selects one case-sensitive named serving tier for this inference
request. AIR resolves the model or alias in the applied generation. The tier on a candidate
engine must fit both the estimated prompt tokens and any explicit minimum. Malformed, unknown
or undersized choices return 400 `invalid_profile` before queuing. The scheduler may share a
ready matching resident or switch to the requested tier under the configured switching policy,
including a smaller tier after drain. Readiness uses the model-bound profile, context and slot
observations. Equal windows with distinct slot counts identify distinct tiers. An explicit switch
that requires a new lifecycle claim waits for named requests and their physical forwards to drain,
including requests selected before their first send. Counted transports check the serving profile
again at submission. Verified transports perform this check after validating the offered request
and before starting issuer checkout. A changed observation fails the ticket with `switch_failed`.
The issuer then enforces its existing route and first-write authorization. Without this header, automatic tier selection follows §7.6.

`X-Inference-Required-Context: <tokens>` declares a positive measured context minimum for the
request. With the header absent, the prompt estimate informs serving-tier selection (§7.6). Every admitted request
carries its ticket id back on the response as `X-Inference-Ticket`, which is how a client reaches
`GET|DELETE /admin/tickets/{id}` (watch / cancel) without scraping the event stream. Body-only
callers (default thread `anon`, priority Normal) work unchanged.

Priority is **not** clamped to the configured default and there is no general caller authentication, so a
caller can currently promote itself. Pinning that at the edge is v0.1 ingress work (§15.1); until
then the deployment posture is loopback-or-behind-your-proxy. `docs/ai-integration.md` states this
to integrators.

The read-only operator dashboard uses the ordinary admin reads and SSE feed (§12.5). Run it
alongside the router on loopback and reach it over an operator-managed tunnel. Loopback is the
default listener and the dashboard's deployment posture. The host listener is configurable.
Ordinary dashboard reads require no dashboard credential. The optional configuration reload
routes use their startup Bearer credential. Target reset also requires its separate opt-in and
a current reload ETag (§6, §7.7).

A feed subscriber is invoked **inside the append**, on the thread that produced the event. A subscriber that throws
**loses its own event and nothing else**: the append completes and keeps its sequence number, the subscribers that did
not throw still receive it, the emitter finishes the transition it was mid-way through, and the fault is counted for
the operator (`/admin/status` → `FeedSubscriberFaults`) rather than rethrown into a stranger's call stack. A subscriber
that *blocks* blocks every producer that appends, because the append holds the ring lock: the feed is a diagnostic
surface, not a hook, and an observer that must not slow a request replays it by sequence instead of subscribing live.

Code that performs I/O on behalf of a subscription handles its own failures, unsubscribes,
and cleans up outside the ring lock. The ring's catch protects the producer and increments
`FeedSubscriberFaults` for each uncaught callback exception. Replay retains the original
event and sequence without adding a fault record.

The shipped error contract is one lowercase body on every surface —
`{"error":{"type":"air_error","code":…,"message":…}}` — and the code picks the status:
`invalid_request` 400, `not_found` 404, `deadline_exceeded` 408, `cancelled` 499, `queue_overflow`
429, `request_body_too_large` 413 (over `max_request_body_bytes`), `engine_error` / `engine_unreachable` 502,
`switch_failed` / `model_unavailable` 503. A switch that outlives `switch_wait_timeout_ms` can
return `switch_failed`. The absence proof and the configured explicit-context proof in §5.5
return `model_unavailable` before queueing, with `queue.rejected` on the feed. A configuration
change can make retry possible. `engine_timeout` 504 covers an engine that accepted the request and then went quiet
past `response_timeout_ms`, **and only where no response has started**: a streaming request has already
committed its `200`, so the same failure reaches the caller inside the stream as one `engine_timeout`
error frame followed by `[DONE]` — a 504 therefore always means nothing was emitted yet, and a caller may
retry one),
`switch_in_progress` 409, `unknown_profile` 400 and `too_many_switch_operations` 503 on the admin side.
The operation lookup's 200 describes the lookup. `Reason` is null on `pending` or `succeeded`.
On `failed` it is the exact closed-set switch category used to choose the action below.
The separate `cancelled` status has `Reason="cancelled"`. Check router availability before retrying
a stopped operation. Immediate POST refusals use the lowercase HTTP error shape.

A ticket refused before queue admission produces one `queue.rejected` with reason `queue_overflow`
or `model_unavailable` and no `request.enqueued`. An accepted ticket settles with one
`request.completed`, `request.failed` or `request.cancelled`. When the queue deadline settles an
accepted ticket, the feed records `request.failed` with a reason sentence and the ticket has error
code `deadline_exceeded`.
These feed events describe ticket outcomes. Committed SSE follows the stream rules in §6.

A `switch_failed` message names the target and, when the caller's own attempt **ended**, ends with that attempt's
reason **category**: one of `no_select_command`, `launch_failed`, `health_timeout`, `refresh_omits`,
`refresh_unanswered`, `window_too_small`, `switch_error`, `no_lifecycle_owner` — **bare, parameter-free, in
parentheses**. Branch on the whole name. It is a member of a closed set with one owner in the source (§5.5), so a name
never arrives as `refresh_omits_<model>` or `health_timeout after 300ms`; a caller that matches those forms is parsing
text it does not own, and the model, the window and the milliseconds are in the sentence around the name or on the
feed. What each name asks of the caller: **fix the configuration** for `no_select_command` (the configuration names no
selector for that model on that engine), `refresh_omits` (the engine answers and never names the model, so the catalog
key does not match what it reports) and `window_too_small` (the model is served in a context window smaller than the
profile asked for — a ladder tier, not a retry); **retry later** for `launch_failed`, `health_timeout` and
`refresh_unanswered`, where the engine did not answer in time; **retry** for `switch_error`, the catch-all for an
attempt that threw or never started; **retry elsewhere** for `no_lifecycle_owner`, where the router cannot load models
into this engine and an immediate retry here repeats the refusal. A wait that **ran out** rather than ending names no
category, because nothing ended: its message carries the caller's own ceiling in milliseconds, the engine's state, and
the fact that the switch is still running. The `code` stays `switch_failed` at 503 whatever the category — the
category changes the caller's action, not the status. Each category is bounded where it is written, and the operator's
full sentence, including anything a lifecycle provider said, stays on the feed: a caller learns which failure it got
and never receives a socket path, an address, or a selector's stderr. A refusal the set does not name is rejected where
the answer is built; if such a refusal ever reaches this surface it arrives as `engine_unreachable` from the request
path's outer catch, and that is a bug to fix at the producer rather than a shape to code against.
The scheduler's wait reasons (`no_engines`, `no_available_engine`) are diagnostics for the feed and for
tests, never wire codes: the only code an expired wait produces is `deadline_exceeded`, and `MapStatus`
deliberately carries no arm for a reason string — an unrecognised code landing on its `500` default is a
bug to fix at the producer, not a contract to rely on.
An upstream engine's own 4xx/5xx arrives as `engine_error` with the engine's message embedded, so a
caller can tell "my request was wrong" from "the engine is down". Every message in that body carries the router's ceiling: the
finished sentence is capped where the ticket becomes terminal and again at the writer that serialises it, so a caller cannot
choose the size of an answer about their own request — whether the long text came from an upstream engine, a URL segment, or a
rejected value in a request body (§10). The router-generated HTTP error JSON entity is at most
**9,856 bytes**. That allows six bytes per UTF-16 code unit of the 1,600-unit reason and 256 bytes
for the remaining envelope and fixed fields. On a committed SSE response, the router error event
and its `[DONE]` event together are at most **9,878 bytes**, including 22 bytes for both event
prefixes, delimiters and the sentinel. The pair's limit excludes preceding keepalives and upstream
data. These limits count encoded body bytes. `ErrorContractTests` and `StreamFrameIdentityTests`
pin the error shape, reported reason and encoded sizes. `docs/ai-integration.md` carries the error
code table for integrators.

## 7. Configuration (TOML)

**Local lifecycle groups.** An engine may opt into a named
`lifecycle_group` with native Docker lifecycle owned by this router. Every member of a group
uses the same daemon and router identity, with distinct inference endpoints. Group size,
client count and catalog size have no fixed two-member limit. A selected cold request closes
new admission to its group while existing transport uses drain; that original queued request
then owns stop, launch and readiness. Each admitted use holds its group charge through actual
response disposal, including detached cleanup after a client timeout. Independent groups can
progress concurrently. After a failed operation each later cold request takes fresh ownership inventory of
every group member. A single identified running resident may be verified for reuse or switched
out. Created and exited containers occupy no serving slots. Multiple running residents or
inconclusive ownership return `switch_failed` with the reason on the event feed. The next
request checks again. For an uncertain write the local provider first inspects the exact
container, then cross-checks its state against fresh inventory. A confirmed, inactive container settles Stop. If Stop never took effect, the next owner can
stop that exact running container again. Pending Delete requires exact absence and can retry
unforced removal after a verified stop. AIR checks an uncertain Create by its unique name.
If the name is absent, any delayed Create can only leave a stopped container because the
failed caller has already returned. An uncertain Start can settle through exact-ID absence or
evidence that the instance started. If the instance remains created, the next owner can remove
that exact ID without force and confirm absence before launching. Each recovery attempt bounds
its cleanup writes and checks ownership again. A refused Start also retains its created ID
for reconciliation. A delayed Create can leave a stopped, owned container, which participates
in ordinary inventory and configured stopped-container retention. The original request's
cancellation and remaining switch budget bound group census, preflight and mutation. AIR
reports caller or execution cancellation as `cancelled` and budget expiry as `switch_failed`.
Cancellation releases an unclaimed target wait. Once this owner claims a target transaction,
its group stays reserved until that transaction unwinds. AIR checks each affected inventory again before stop and checks sibling vacancy before launch.
The group coordinates this router's requests. Other clients of the Docker daemon can change
containers between API calls. Local-group failures do not enter the ordinary
engine's persistent target quarantine. The failed inference is never replayed. The switch
wait budget starts at exact queue removal and is not renewed during preparation. Group-bound
engines reject direct administrative switching and configuration reload in this initial binding.

On first admission after router startup, a matching AIR-owned Docker container can serve the
original request once its launch settings and backend readiness are verified. If either check
finds a mismatch, AIR can replace that container once. Before Stop, AIR checks the other group
members for vacancy and rechecks the incumbent's ownership, container ID, start time and restart
count. Inconclusive Docker observations get one more discovery pass within the original request
budget. Cancellation or expiry before Stop prevents replacement. The newly launched backend must
pass readiness before the waiting request is dispatched.

This opt-in coordinates requests and labeled containers owned by this router on the existing
Docker daemon. It does not certify exclusion of unrelated GPU processes, other Docker clients,
or foreign containers. Operators must assign overlapping exclusive resources to the same group.
The existing commissioned path and its stronger host-enforcement contracts remain separate.
Live Pi acceptance verified a Qwen request completing before the original queued DeepSeek
request selected its target, stopped the incumbent and streamed its reply. Both Pi clients
disabled automatic retries. Container state, native GPU process membership and authenticated
backend model/context/slot readback corroborated residency. Three shared engines and a fourth
independent engine are covered by focused multi-client fixtures; the live pair is an input, not
a cardinality limit. The detailed host transcript is held outside the repository.


The server loads its startup file before serving. A file-backed server can optionally apply changes
from that same path as described in §7.7.
For HTTP SSE, `stream_buffer_limit_bytes` is a top-level nonnegative byte count and
`stream_idle_timeout_ms` is a nonnegative integer under `[engine.<name>]`. Each defaults to zero
and independently disables its limit. Changes to the root limit or a retained engine's idle timeout
require restart. A newly added eligible external engine may declare its own idle limit.
`lifecycle_owner` accepts `external`, `delegated` and `router` in TOML and JSON. Remove `remote`
when setting it, including `remote = false`. Explicit `external` requires no controller or selector.
Explicit `delegated` requires a usable selector and no Docker controller. Explicit `router` requires
Docker configuration, a loopback inference endpoint, a local Unix Docker socket and a dedicated
single-engine configuration. Host-wide fencing and native lifecycle on a managed peer are still pending.
Programmatic explicit owners use the same provider and topology checks. A standalone runtime with
an empty engine map counts as one engine. A programmatic `Remote = true` conflicts with an explicit
owner, while `Remote = false` is the public default.

Existing configurations may keep `remote`. `remote = true` maps to `external`, including when
accepted legacy controller settings are present. With `remote = false` or omitted, a configured
Docker provider maps to `router`, and a usable selector or explicit exec mode (`"exec"` or `"delegated"`) maps to
`delegated`. With none of these declarations, the engine maps to `external`. An attempt to switch
such a legacy selectorless engine now returns `no_lifecycle_owner` where it previously returned
`no_select_command`. Existing explicit exec configurations and engines with selectors still return
`no_select_command` for a target they cannot select. Requests for an already-loaded model continue
to forward in all of these cases. Legacy native Docker retains the existing per-engine switch
behavior, including a peer inference endpoint, a Docker daemon reached over HTTP(S), or a router
with multiple engine definitions. This compatibility path acts only on the configured daemon and
the engine's label-owned containers. It supplies no proof of host-wide occupancy or exclusion.
Fenced managed-peer actuation requires an enrolled host controller.

`docker.socket` accepts Unix socket paths with or without `unix://` and HTTP(S) daemon addresses under
the authority rules above. Router-owned Docker rejects `npipe:` and bare Windows pipe names, including
local, server, device and extended UNC spellings with slash or backslash separators. The diagnostic
identifies the `docker.socket` field without echoing the address. An explicit `unix://` declaration
retains its Unix path interpretation. A legacy `remote = true` engine can retain an inert Docker
declaration and forward already-loaded models without binding a lifecycle provider.

An engine can set `models_context_window_path` in TOML or JSON when its models endpoint reports
the effective serving context as a positive signed 64-bit integer in each individual loaded row.
The dotted object path is relative to that row. Leave it unset when the backend exposes an
architecture limit, an engine-wide gauge, or a value whose serving meaning is unverified.

This managed example uses one engine definition and two model instances that take turns serving
at the same endpoint. Supply an image already present on the Docker host whose entrypoint accepts
`--model` and `--profile` and serves a single model on container port 8080. It must
report the canonical model ID at `/v1/models` and expose `n_ctx` and the configured total slot
count `total_slots` at `/props`. The image reference, model IDs and profile tiers are illustrative.
The image must implement each tier's context and slot settings. Fill all three pins from the
intended image on that daemon. Keep the endpoint port and `host_port` aligned for this local
binding. The [shipped native example](samples/router.native-docker.toml) illustrates these fields.
Reproducing every legacy selector configuration is tracked in TASK-111.

```toml
listen = "http://127.0.0.1:8321"
default_model = "example.chat-v1"
auto_switch = true  # allow the router to load a catalog model through its lifecycle provider

[scheduler]
max_inflight_per_engine = 4

[engine.strata]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18021/v1"
lifecycle_owner = "router"
lifecycle = "docker"
models_endpoint = "/v1/models"
health_probe = { path = "/props", expect = "total_slots>0", context_path = "n_ctx", slots_path = "total_slots" }

[engine.strata.docker]
image = "example/inference-backend:1"
image_id = "sha256:1111111111111111111111111111111111111111111111111111111111111111"
image_repo_digest = "example/inference-backend@sha256:2222222222222222222222222222222222222222222222222222222222222222"
image_platform = "linux/amd64"
router_id = "example-router"
socket = "unix:///var/run/docker.sock"
api_version = "v1.44"
command = ["--model", "{{model}}", "--profile", "{{profile}}"]
host_ip = "127.0.0.1"
host_port = 18021
container_port = 8080
stop_timeout_ms = 30000
call_timeout_ms = 300000
wait_health = true
wait_timeout_ms = 600000

[engine.strata.catalog."example.chat-v1"]
aliases = ["chat"]
default_profile = "32k-4"
serving = [
  { name = "32k-4", max_context = 32768, max_slots = 4 },
  { name = "64k-2", max_context = 65536, max_slots = 2 },
]

[engine.strata.catalog."example.code-v1"]
aliases = ["code"]
default_profile = "32k-2"
serving = [{ name = "32k-2", max_context = 32768, max_slots = 2 }]
```

**Optional offline Docker launch options.** In `[engine.<name>.docker]`, set `entrypoint` to one
literal, clean absolute Linux executable path inside the image. AIR sends it as a one-element
Docker `Entrypoint` array. `command` still supplies the separate Docker `Cmd` argument array.
The executable path keeps its literal spelling and receives no host path lookup or template
substitution. Set `ipc_mode = "host"` to project only Docker `HostConfig.IpcMode`. Other explicit
IPC modes are invalid. Omission of either option leaves its Docker defaults in effect.

JSON and TOML reject explicit nulls or duplicate launch options. JSON also rejects shadowed
opt-in ancestry and ownership fields, and requires canonical field casing for opted-in engines.
These options are available for offline projection only, even when the engine has no resources
or per-tier launch declarations.

For the paired-GPU HyperQwen launch options, set engine-wide Docker `init = true`,
`restart = "unless-stopped"` and `log_rotation = { max_size = "20m", max_files = 3 }`
independently. The create payload uses `HostConfig.Init`, `RestartPolicy` with a zero retry
count, and `LogConfig` with an empty driver type. The daemon selects its default driver.
The rotation size is a canonical positive Int32 decimal followed by lowercase `m`. The
file count is a positive Int32. Both rotation fields are required. A supplied init or restart
value must match the form above. Omitted options leave their fields absent from the payload.
These keys belong in the engine's Docker block. A model-level Docker table containing any of
them is invalid.

For an engine that declares per-tier Docker plans or engine-wide offline launch options,
`host_port` adds the corresponding container TCP key to root `ExposedPorts` with an empty
object value. The same key in `HostConfig.PortBindings` supplies the configured host address
and port. This applies to every model on the opted-in engine, including siblings that use
only common settings. A bare port binding or one with only common resources omits the root
field. Omitting `host_port` also omits root exposure.

Where a launch plan freezes the serialized create request, the added exposure changes the
launch settings digest and requires evidence for the new envelope. The existing Docker
declarations gate startup and mutation across the engine, in serving-tier, launch-option,
then resource refusal order. Docker policy describes create intent. Before
activation, verify that the effective daemon log driver supports both `max-size` and
`max-file`. Refuse activation if the driver or its support for either option is unknown
or incompatible. Restart authority, image association and authenticated serving-instance
admission also require host verification.

**Optional Docker resources.** Use `[engine.<name>.docker.resources]` or the
equivalent JSON object to validate a resource request offline. Keep the table omitted in
an active native Docker configuration while resource-bearing activation is gated.

| Field | Accepted input |
| --- | --- |
| `gpu` | `{ all = true }` or `{ device_ids = ["1", "0"] }`. IDs are distinct canonical numeric strings. The request retains their order. |
| `device_paths` | HIP requires `/dev/kfd` and at least one distinct `/dev/dri/renderD<N>`. Paths map to the same container paths. Choose either raw HIP devices or an NVIDIA request. |
| `bind_mounts` | A nonempty list of `{ source, target, read_only }` entries. Both paths are clean absolute Linux paths outside control directories. Container destinations must be distinct and may not overlap. Set `read_only` explicitly. |
| `nano_cpus` | A positive integer in billionths of a CPU. |
| `memory_bytes`, `memory_swap_bytes` | Memory is at least 6 MiB when set. The positive swap-inclusive total requires memory and is at least that value. Equal values permit no extra swap. |
| `shm_size_bytes` | A positive byte count. Omission leaves Docker's shared-memory default. |
| `ulimits` | A nonempty list of distinct `core` or `memlock` entries with `name`, `soft` and `hard`. Limits are nonnegative or `-1` for unlimited, and soft must not exceed hard. |

An explicit resource object must contain a setting. The loader refuses unknown or
duplicated resource fields and explicit JSON nulls. Bind sources are literal paths
on the Docker host. The create request uses structured binds with source creation disabled.

**Offline Docker settings per serving tier.** A structured serving entry on a native Docker engine
can declare a command, environment assignments, binds, memory limits, a single NVIDIA device,
shared memory or a Docker health check.
TOML inline and nested tables and JSON objects use the same fields.

```toml
[[engine.strata.catalog."example.chat-v1".serving]]
name = "64k-2"
max_context = 65536
max_slots = 2

[engine.strata.catalog."example.chat-v1".serving.docker]
env = ["CONTEXT=65536", "PARALLEL=2"]
bind_mounts = [{ source = "/srv/example/weights", target = "/models", read_only = true }]
```

A tier's `docker.healthcheck` accepts a nonblank, control-free `command`. The optional
`interval_ms`, `start_period_ms` and `timeout_ms` fields are integers in 1 through
9,223,372,036,854. Each supplied duration becomes a signed 64-bit nanosecond value.
Optional `retries` accepts a positive signed 32-bit integer. The top-level Docker `Healthcheck.Test` is
`["CMD-SHELL", command]`, with the decoded command preserved literally. An absent probe or
optional field is omitted from the create request to preserve Docker inheritance and defaults.
The start period controls when failures count toward an unhealthy status. The health check is
available only on a serving tier, and any declaration keeps the whole engine offline for projection.

For an engine using this feature, model IDs and aliases match case-insensitively and must
identify one canonical model. An opted-in model's serving names must be distinct under ordinal
comparison. Its omitted selection uses its declared default or its sole tier. Multiple tiers
require a default, and `profile` and `default_profile` must agree if both are present.

A serving row with its own nonempty native Docker target may list explicit profile spellings
in `aliases`, such as `aliases = ["max-4"]` on the `256k-4` row. Match aliases by ordinal
comparison. Each alias must be nonblank and contain neither control characters nor edge whitespace.
Within one model an alias must be unique among aliases and canonical tier names. The loader
rejects null, empty or malformed lists and shadowed declarations in JSON and TOML.
`profile` and `default_profile` use canonical names only. An unknown explicit spelling
refuses resolution. Aliases share the canonical launch plan, template inputs and labels.
The serving ladder and offline-only activation gate continue to use canonical tiers.

The loader freezes the effective inputs for each tier of an opted-in model. A tier's
`docker.command` replaces the common Docker `Cmd` in full. Supply a nonempty array of strings
without NUL characters. Each argument preserves its literal text, including whitespace, empty
strings and placeholder spellings. An omitted tier command inherits the common command. The
loader validates common command arguments on an opted-in engine even when tiers override them.
Only engine-wide `command` and `env` expand `{{model}}`, `{{profile}}` and `{{endpoint}}`
in the original template. Replacements are literal identifiers. The entrypoint remains a
separate engine-wide value. Tier environment assignments are literal and follow common
assignments in the payload. Assignments use a portable
`KEY=value` name and permit empty values. Duplicate names after common expansion are refused.
Common mounts precede target mounts. Destinations in the combined list must be distinct.
A tier bind may declare `nested_under` naming an exact, earlier bind destination that is a
strict path-component ancestor. Both binds must be read-only, and the parent cannot itself be
nested. Every other overlap is refused. Common bind entries have no `nested_under` field.
The annotation is used during validation and omitted from Docker JSON. Effective sources
and destinations must be outside protected control paths and their ancestors.
These checks are lexical. Host qualification verifies actual mounted paths and effective read-only
enforcement, including any recursive mounts.
Image pins, network bindings and resource limits without tier fields remain engine-wide.

A tier's `docker.gpu = { device_ids = ["1"] }` replaces the entire common NVIDIA request.
Supply exactly one canonical numeric device ID as a string. Omission inherits the exact
common selection, including device order or the all-GPU request. The loader validates the
common request even when a tier replaces it. A tier NVIDIA request cannot coexist with
common HIP `device_paths`. Use `gpu` only inside serving-tier `docker` or engine-wide
`docker.resources`. A GPU-only tier opts the whole engine into the offline mutation gate.
Before activation, verify the actual device mapping, toolkit and driver support, allocation
and serving fit on the daemon host.

A tier's `docker.shm_size_bytes` replaces Docker's common `ShmSize` with a positive Int64
byte count. Omission inherits the common value or leaves the field absent. Common values
remain validated even when overridden. JSON and TOML check the legal tier and common
resource locations before binding and reject shadowed or misplaced declarations. An
explicit tier null is invalid. A SHM-only tier opts the engine into the same offline gate.

The HyperQwen selector's default 32-GiB application cache projects to 36 GiB of Docker
shared memory. The selector's 1- and 48-GiB cache endpoints project to 5 and 52 GiB.
Changing cache or sequence choices requires resolving and configuring another launch vector.
Docker SHM describes the container limit. Before activation, verify the effective size
and check available host RAM for the selected cache plus a 10-GiB reserve after the
predecessor stops. Device mapping, effective logging, readiness and host authority remain
independent qualifications.

A tier's `docker.memory_bytes` and `docker.memory_swap_bytes` are exact 64-bit byte counts.
Memory must be at least 6 MiB. `memory_swap_bytes` is total memory plus swap, at least the memory
limit. Equal values permit no additional swap. The loader resolves the pair as follows.

- With neither field, inherit the engine's entire memory pair.
- With both fields, replace the entire engine pair.
- With memory alone, set that memory limit and omit Docker's total only if the engine has no total.
- A tier with only a total is invalid.

Omitted values are absent from the Docker create request. Leave engine memory limits absent when
some tiers need no explicit cap. Docker applies its defaults to omitted values.

For engines using tier Docker declarations, JSON requires canonical casing for recognized
common Docker fields and rejects shadowed ownership settings, delegated selectors and target
ancestry. It also rejects duplicate or unknown target members and explicit null values in a
target tier. TOML uses the parser's existing case-insensitive property matching and rejects
duplicate target members. Existing configurations without tier Docker declarations retain
their previous behavior.

Requirements: comments preserved for round-tripping is **not** required;
schema errors fail fast at boot with path-precise messages; unknown engine
kinds rejected. Config binding is source-generated (§9) — no reflection.

**Dotted model IDs must be quoted.** TOML treats `.` as a table separator, so
`[engine.strata.catalog.example.chat-v1]` nests under `example`. Write
`[engine.strata.catalog."example.chat-v1"]` to keep the full ID as the catalog key.

**Offline validation.** Save the complete block as `router.managed.toml` and run
`air config --config router.managed.toml` (§11). The command should exit successfully and list
`strata` with both catalog IDs and their profile ladders. It only loads configuration, so it
needs neither Docker access nor the example image. Image presence, launch and readiness are
runtime checks. Use `samples/router.safe-smoke.toml` for a runtime smoke test. The selector tables
in `samples/router.toml` demonstrate legacy exec compatibility.

**URI composition.** Engine request paths are joined against `endpoint` by one rule, applied
identically to completion calls, the health probe and the catalogue probe
(`EngineRuntime.Combine`): a value starting with `/` is **absolute from the server root**
(`"/props"`, `"/models"` — what llama.cpp-style servers expose), while a bare value is appended
to the endpoint's own path (`endpoint = "…/v1"` + `"chat/completions"` → `/v1/chat/completions`).
A `?` in the value supplies the query string; without one, the endpoint's query survives.
Trailing slashes on `endpoint` are absorbed rather than doubled. Completions are always composed
against the endpoint path, so mounting an engine at `…/v1` reaches `/v1/chat/completions` — before
this rule the base path was dropped and every such engine answered 404 (§14 revision v0.9).

### 7.6 Serving profiles and context-window escalation

GPU memory per slot scales with `n_ctx`, so an engine that always serves the maximum window
costs concurrency. A model's **serving profiles** define its context tiers and slot counts.
This catalog entry is the chat model from the managed configuration in §7:

```toml
[engine.strata.catalog."example.chat-v1"]
aliases = ["chat"]
default_profile = "32k-4"
serving = [
  { name = "32k-4", max_context = 32768, max_slots = 4 },
  { name = "64k-2", max_context = 65536, max_slots = 2 },
]
```

The Docker `command` template passes the chosen profile name to the image as `--profile`.
A plain model switch uses `default_profile`. A switch targeting `64k-2` passes that name to the
new container. The image configures the window and slots, and the router uses `health_probe.context_path`
and `health_probe.slots_path` to read those gauges. Dotted paths traverse object keys and zero-based array indices, for example `data.0.max_model_len`. Invalid or out-of-range indices, missing fields and null values yield no gauge; numeric object keys remain literal keys. The probe checks described in §5.3.1 apply.

Semantics:

- **Tier selection demand**: use a positive `options.required_context` or the HTTP
  `X-Inference-Required-Context` minimum when supplied. Otherwise use
  `ceil(prompt_chars / scheduler.chars_per_token_estimate)` (default 4.0). The estimate guides
  tier selection. It can overestimate the real rendered token demand, so only an explicit
  minimum can trigger the configured context refusal before queueing (§5.5).
- **Explicit context minimum**: a positive declaration stays with the exact selected ticket.
  A loaded route needs a current measured window for that model at least as large as this
  minimum, even without a named tier. Exact take revalidates the capability and slot
  observation used for an explicit minimum or tier decision. A switch must prove a fresh
  model-bound window that fits both the request's explicit minimum and any selected tier.
  Trust mode reads fresh state for either requirement. Each joined waiter applies its own
  minimum to the measured result, including when it receives a committed verdict after
  shutdown. A same-target waiter whose minimum exceeds the committed window cannot use
  that proof to forward. A tier label, static configuration and prompt estimates alone
  supply no measured capacity. Offline verified dispatch requires a fitting positive measured
  window and positive reported slots on an explicitly selected, resolved-default or reused
  named resident profile. Public gauges and roster can veto eligibility. They supply no
  additional authority. An ordinary request can reuse a fitting verified resident tier
  independently of the catalog's launch default. The injected host path can attest to exact-instance roster
  readiness today. A switch with an explicit minimum, selected named tier or a resolved
  named default requires bound capacity evidence and refuses before entering the host
  transaction until that evidence is supported.
- **Admission**: an engine's live capacity is the *tighter* of
  `scheduler.max_inflight_per_engine` and the slots it reports (`total_slots`);
  until the gauge is known the configured ceiling alone applies. A profile
  switch therefore lowers admission as soon as the engine reports fewer slots.
- **Escalation**: the scheduler selects the *smallest* profile with `max_context >= required`.
  A loaded model needs an observed window that meets that tier. A smaller reported window can
  trigger a switch when `auto_switch = true`. An unknown window on a loaded model leaves a
  capacity-dependent request waiting for evidence. A healthy loaded model can still serve a
  request with neither an explicit minimum nor a required profile. The built-in scheduler
  skips an engine with a nonempty ladder and no fitting tier before it considers the observed
  window. An engine with no applicable ladder can serve an explicit minimum on a loaded route
  with a fitting model-bound observed window.
  A failed switch returns `switch_failed` (§5.5, §6).
- **Binding**: an engine-wide context window binds to a single non-alias, loaded roster row
  that matches a canonical catalog key case-insensitively. An additional upstream row with a
  string ID makes the window unbound. Total slots can still be reported. The inferred profile
  requires a unique matching tier in the bound model and serves as a display label.
- **Observability**: `engine.profile` events report committed positive window or inferred-profile
  changes. `EngineStats` reads `active_profile` / `active_context_window` / `active_slots` from one snapshot.
- **Non-goal for now**: automatic *de*-escalation (shrinking a window when big-context
  demand goes away). Escalation is demand-driven; release policy is an open question (§15).

The shipped Maya example keeps GLM, HY4 Flash and MiniMax M3.1 Flash in the same
externally owned engine.
It discovers loaded models through absolute `/v1/models` and reads the engine-reported
window and slots at `/props.default_generation_settings.n_ctx` and `/props.total_slots`.
HY4 uses canonical `hy4-flash` with request aliases `hy4` and `hy4-flash-maya-xs`.
The sample declares `32k`, `64k`, `96k`, `128k`, `256k`, `512k` and `1024k`
at 1,024 tokens per k and one slot per tier. An omitted launch tier resolves to
`96k`, a 98,304-token window sized for 65,536 input tokens plus 32,768 output tokens.
`32k` is an explicit smaller launch selection. A 32,768-token observation cannot
satisfy the 96k launch target or authorize a 65,536-token request.
An unloaded row cannot authorize a send even when the model probe succeeds and
`/props` reports positive slots.

The inspected HY4 selector accepts `32k` alone and still defaults to it. The other six AIR targets have
distinct offline create projections and probe expectations. The selector's argument choices
exclude `64k`, `96k` and `1024k`. Its HY4 profile map excludes all six wider tiers. Native launch
projection remains gated. Host context fit, memory, artifact identity, exact-instance
readiness and lifecycle authority need separate qualification.

MiniMax uses canonical `minimax-m3.1-flash` with request aliases `minimax` and
`minimax-maya-xs`. It declares the same seven one-slot context tiers as HY4. Its
explicit safe fallback is `32k`, its lowest normal selection is `64k`, and the omitted
launch tier resolves to nominal `96k` (98,304 tokens). The external owner selects the
actual launch. AIR binds capacity to the loaded model and its observed window and
slots. The archived MiniMax selector projection covers `32k` only. Wider launch
settings and host fit require separate qualification. With the pinned Maya eight-token
reserve, nominal 96k leaves 98,296 usable tokens and 32,760 output tokens after a
65,536-token rendered prompt. The literal full split needs at least 98,312 nominal
tokens on that path and separate workload qualification.

### 7.7 Runtime configuration reload

At idle, authenticated apply can add native Docker engines to an existing local lifecycle group
and add aliases to its existing catalog models. Existing engines retain their runtime, resident
and failure state. Reload publishes a single generation and performs no Docker lifecycle operation.
Queued requests, active switches and outstanding transport uses return the existing busy response.
Changes to existing launch settings or removal of a managed engine still require restart.


Set `ARBOREAL_ROUTER_RELOAD_TOKEN` before starting a file-backed server to enable the reload
routes in §6. The startup file is selected by the explicit command-line path, then
`ARBOREAL_ROUTER_CONFIG`, then `AIR_CONFIG`, then `~/arboreal/inference-router/router.toml`. The server captures that absolute
path and reloads from it. The operator replaces the file and supplies the ETag from an authenticated
GET to POST. The request supplies neither a file path nor its contents. Programmatic configuration
overrides and servers started without the reload credential have no reload routes.

| Change | Apply rule |
| --- | --- |
| `default_model` | Live. Applies to new requests with neither an explicit model nor thread affinity. |
| Addition or removal of externally managed engines | Router-wide work idle only. Can combine with a new default in the same apply. |
| Addition of native engines to an existing local group or aliases to existing group models | Router-wide work idle only. |
| Other retained engine edits, relative engine order changes, other native or delegated membership changes, and all other settings | Restart required. Includes endpoint, credentials, lifecycle plans, listener, scheduler, limits and configured threads. |

An engine membership edit preserves the relative order of retained engines and appends additions in
ordinal name order. A known model token cannot resolve to a different canonical model in the same
apply, even if its previous engine is being removed. Conflicting new aliases, case-folded engine
name collisions and duplicate JSON properties are refused. The parser and normal startup topology
checks also apply to candidate files. Restart-only declarations preserve explicit values and ordering
where the mapper uses it.

A changed effective default needs a configured model in the candidate or a retained, healthy,
unclaimed external engine reporting it at publication. The reported evidence is checked under
the same transition gate used by that runtime's capability writers. Managed defaults use immutable
configured declarations. External health discovery still governs dispatch. A new engine starts
with unknown capability and has no discovered model rows until it reports them. Existing thread
affinity remains in effect after topology changes. When the model is known unavailable, a new
request for it gets the ordinary refusal.

Router-wide work idle excludes queued tickets, logical selections, physical inference, accepted
admin switch waits and claimed switches. Physical inference remains active through disposal of a
late response after the ticket times out. A topology apply checks idle and fences removed engines
before the new map is visible. A busy apply leaves every affected engine open. An already-issued
health read may finish cleanup, but a fenced runtime cannot start the next HTTP leg or publish its
result. An active publication from a removed engine makes the apply busy until its event append
finishes. Busy and invalid candidates preserve the current generation.

Decision 75 bounds decision 50's startup-only assumption to each published engine definition.
The router retains the runtime and definition of every unchanged engine. An accepted switch and
any joined waiters use the target resolved before claim. An accepted stream keeps its selected
runtime and upstream bytes. Prospective dispatch revalidates the current generation and the exact
runtime before taking the ticket. Unrelated applies retain the native runtime's existing latches.

Set `ARBOREAL_ROUTER_TARGET_RESET_ENABLED=true` before startup to opt in to exact target reset
(decision 77). The file-backed server also requires `ARBOREAL_ROUTER_RELOAD_TOKEN`. Supplying
only the token enables reload alone. Reset checks the authenticated ETag against the startup
instance and current configuration generation when it removes the exact latch. It issues
no lifecycle operation and leaves unresolved native effects and host fencing in force.
Native definition changes still require a restart. A future managed reload must preserve the
latches for unchanged effective target definitions.

## 8. Prefix normalization & affinity

- Normalization: strip reasoning-tail markers per engine config; lowercase
  ASCII; collapse whitespace; system-prompt templates are keyed by their
  first message content hash. `PrefixTrie` (per engine, bounded LRU) stores
  hashed, normalized message-prefix extents: `hash(parentHash, msgUtf8)`.
- Affinity hint from the client (`X-Inference-Affinity`) is trusted only to
  *seed* the trie lookup, never to record unnormalized data.
- The trie is also the future home of suffix-extent records
  (`SuffixExtent{hash, len, engine}`) for §5.4's suffix scoring.

## 9. AOT strategy

- **All publish targets** compile with `PublishAot=true`, `TrimMode=full`.
- **JSON** (research-dispositioned): every DTO registered in one
  `[JsonSourceGenerationObject]`-style `AirJson` context (per-assembly
  contexts: core DTOs, admin DTOs, delta DTOs). Leave `GenerationMode` at
  default (metadata + fast-path) — deserialization/async/polymorphism need
  metadata; only the tiny token-delta struct path may use `Serialization`
  via `JsonSerializer.Serialize(Utf8JsonWriter, …)` into a reusable UTF-8
  buffer, framed as SSE (no per-token strings). Server registers the context
  via `ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain
  .Insert(0, AirJson.Default))`. No runtime-polymorphic JSON;
  `[JsonDerivedType]` only where a union is genuinely needed.
- **TOML**: bind via source generation — preferred: **CsToml** (Cysharp)
  source-generator binding (its .NET 11 AOT status verified
  before adoption); fallback: Tomlyn DOM read path + hand-written field
  mapping (reflection-free by construction) if CsToml proves incompatible.
- **Server surface** (research-dispositioned): build with
  `WebApplication.CreateSlimBuilder` (RDG request-delegate generation is
  automatic under `PublishAot`); statically declared `MapGet/MapPost` typed
  handlers returning `TypedResults.*` / `Results<Ok<T>,…>` unions; JSON body
  binding via source-gen resolvers only; no `[FromForm]` complex binding.
  SSE: prefer `TypedResults.ServerSentEvents(IAsyncEnumerable<SseItem<T>>)`
  (ASP.NET Core ≥10, AOT-compatible) with registered delta metadata;
  fallback path writes `data: {json}\n\n` frames to `Response.BodyWriter`
  honoring `RequestAborted` + keepalives. OpenAPI: built-in
  `AddOpenApi/MapOpenApi` is AOT-safe (pin 3.1 schema if tooling wants);
  Swashbuckle is not AOT-safe — not used.
- **Client** (research-dispositioned): `HttpClient` JSON via type-info overloads
  only — `PostAsJsonAsync(uri, req, AirJson.Default.Request)`,
  `ReadFromJsonAsync(…, JsonTypeInfo<T>)`, `JsonContent.Create(…, ti)`;
  upstream SSE with `HttpCompletionOption.ResponseHeadersRead` + separate
  SSE framing parse.
- **Build flags**: library projects get `<IsAotCompatible>true`; shipping
  hosts get `<PublishAot>true` + `<JsonSerializerIsReflectionEnabledByDefault>
  false</…>`; per-RID publish + smoke tests in CI; treat IL2026/IL3050/RDG
  diagnostics as build blockers.
- **No reflection-based DI**: server wires an explicit `RouterHostOptions` →
  `IServiceProvider`-free composition root (`AirHost.Build()`); no dynamic
  assembly loading, reflection scanning, or expression compilation.
- **Logging**: `Microsoft.Extensions.Logging` with source-generated logging
  (`[LoggerMessage]`), no reflection fallback providers.
- **Process/exec** lifecycle uses `Process` — AOT-safe (no AppDomain hacks).
- **CI check**: `dotnet publish -r linux-x64 -c Release /p:PublishAot=true`
  for all three shipping projects must exit 0 with **zero trim warnings**
  (run with `-p:TrimmerSingleWarn=false` so nothing is folded away).

### 9.1 Performance/style conventions (CySharp-adjacent)

The digest below is drawn from the public evidence base of the Cysharp-style libraries
(MessagePack-CSharp, ZLogger) plus general .NET high-performance and API-design guidance.
Applied here:

- **Wire/config schemas compile-time explicit**: parse + validate config at boot into
  immutable domain types; never parse on the request hot path.
- **Emit bytes, not transient strings**: request bodies are forwarded as raw
  `ReadOnlyMemory<byte>`/`PipeReader`/`PipeWriter` frames; no UTF-8↔UTF-16 round trips on
  token deltas; never retain a borrowed span across an `await` (streaming chunks are copied
  out of the read buffer before the channel write).
- **No avoidable allocation in the loop**: pooled/stack buffers in the prefix normalizer and
  trie chain hashing (FNV-1a over a `Span<byte>` instead of MD5 + a UTF-8 array per message).
- **Logging is bounded I/O**: generated `LoggerMessage` delegates (CA1848 on), redact
  prompts/tokens/keys; disabled-level fast path.
- **Operational discipline**: bounded channels, explicit capacity reservation at dispatch,
  cancellation/deadlines on every wait, immutable route snapshots, backpressure by queue cap.
- **Benchmarks gate claims**: allocations/request, allocations/token, tail latency, and
  streaming throughput under realistic concurrency. Wired: `bench/Arboreal.InferenceRouter.Benchmarks`
  (JIT Release harness, one shared fake engine per layer, concurrency 1/4/16) with results and
  caveats in `docs/benchmarks.md`. As of the 2026-11-16 run: routing adds **58–91 µs p50 and
  ~15 KB/req** (flat from concurrency 4 to 16), the full HTTP path **217–996 µs p50 and
  26–32 KB/req** (the top of that range is Kestrel/SSE framing saturating, not routing), the SSE
  relay **58–102 µs p50 and ~25 KB/req**. Not yet measured: allocations per output token,
  AOT-published binaries, real engines (prefill interference, slot exhaustion, §7.6 escalation
  under load).

### 9.2 Analyzer stack (integrated)

Root: `global.json` (SDK 11.0.100), `Directory.Build.props` (`AnalysisLevel=latest`,
`AnalysisMode=Recommended`, `EnforceCodeStyleInBuild`, `TreatWarningsAsErrors`,
`CheckForOverflowUnderflow`, `Nullable`), `Directory.Packages.props` (central versions —
the SDK already ships `Microsoft.CodeAnalysis.NetAnalyzers`, so it is *not* installed
again), `BannedSymbols.txt` + `.editorconfig` (`RS0030 = error`: `Activator`,
`Reflection.Emit`, and the reflection-based `JsonSerializer`/`TomlSerializer` overloads).
Per shipping project: BannedApiAnalyzers 5.6.0, Meziantou.Analyzer 3.0.341,
IDisposableAnalyzers 4.0.8, AsyncFixer 2.1.0, CSharpIsNullAnalyzer 0.2.19; tests add
xunit.analyzers 2.3.0. Severities tuned once, in `.editorconfig`, each with a reason
(`CA1865`/`MA0001` collide on single-char prefix tests; `IDISP004` is dominated by
`JsonElement`-enumerator false positives, so `CA2000`/`IDISP001` stay errors).
Status: **0 warnings / 0 errors with warnings-as-errors, and an AOT publish with
`TrimmerSingleWarn=false` reports no trim warnings.** Narrow, commented `#pragma` /
`[SuppressMessage]` sites are the only suppressions.

## 10. Observability

`RouterEvent` ring buffer (per running router, ordered, monotonic `Seq`, replayable; capacity 4096).
Replay is **exclusive** on its cursor: `since=n` means "after n". A cursor inside the retained window
gets every event still held for what was asked — there is no smaller cap than the ring itself — and a
cursor ahead of the ring (a client reconnecting with a sequence from a previous router lifetime) is
clamped to the current head, so the live tail still flows instead of a feed that can never deliver
anything. Replay begins at the oldest retained record when the cursor predates that window. A subscriber's
snapshot and its live subscription are taken together, so an event appended between the two is neither lost nor
delivered twice. The feed is also **causally ordered**: every terminal outcome — an engine switch, a
completed, cancelled or failed request — is appended before the caller waiting on it is woken, so a
client that awaits its own ticket and then replays its history, or an operator tailing `/admin/events`,
never arrives a moment before the explanation does.

Each HTTP event-feed subscriber has a 256-event live buffer. On overflow the feed stops accepting new matches and
ends after it sends the queued matches if the reader stays connected. Reconnect from the last
`Seq` received. Replay can recover the matching records that the ring still holds. A sequence hole
in unfiltered replay can identify missing records within the same router lifetime. A filtered feed
normally skips some global sequences, so a jump is inconclusive. An ended feed gives a reconnect
signal. Its cause and any loss count remain unknown from the ending alone.

The ring starts a new sequence on restart and carries no epoch. A cursor ahead of the new head
is clamped as above. A cursor from a previous lifetime can also fall within the new sequence range.
Refresh the current admin snapshots when continuity is lost or uncertain. A shared `stream.gap`
event is declined because loss is subscriber-relative and a shared record would consume ring
capacity without identifying what the subscriber missed.

Live subscriptions are bounded by `max_event_subscribers` (256 by default; the count includes the
router's own internal subscription, so the key must be at least 2 to let one client feed through), and
a feed that arrives when the ring is full is refused with `503 too_many_subscribers` **before** any SSE
headers go out. A subscription ends when the router shuts down, and a caller that hangs up stops being
delivered to. Field names on the wire are PascalCase (§6).

The feed's text is a **bounded excerpt**, not a transcript. Identifiers — a model id, an engine name, a profile
name, a thread id — are carried at reporting length: **128** characters in total, which is a head of 96 characters
plus the cut marker. The marker names how much was dropped and carries a fingerprint of the whole value — the first eight
hex digits of a 64-bit FNV-1a, so **32 bits** — which keeps two identifiers that share a head (the caller chooses the head of
their own id) distinguishable **in the record that holds them**, without the feed holding the caller's text. That is a
diagnostic distinguisher for an operator reading a feed, not an identity and not an integrity check: 32 bits collide — on the
order of one colliding pair in 4.3 billion values taken at random — and a value written literally as another value's head plus
its marker is already at reporting length, so the cap leaves it untouched. Correlating one request across its records is
`TicketId`, which the router generates and never cuts; a stable opaque id for arbitrary caller-chosen values would need an
assignment map with an eviction rule, and a new wire identity field is an owner decision, not a side effect (§6.1,
decision-45). A cut never lands between
the two halves of a surrogate pair: a head ending in a lone high surrogate is text that cannot be encoded, printed or
compared back to what it came from. The marker fits inside the 32 characters the cap reserves, which is what makes
the total a stated bound rather than an approximation, and a budget too small to leave a head is refused. Where the
**head** is what an operator acts on rather than a total — an engine's reported model names in a refusal — the number
is a head of **24** characters with the same marker, enough of a provider's spelling to tell one quantisation tag
from another. A provider's reason is capped at 160 and its action line at 200, a quoted `/models` answer at eight
names inside a 500-character summary, and one reason sentence at **1600**. The ring's cap on **entries** has never
been a cap on size, and every one of these values is chosen upstream — by an engine, a provider, or the request body.

Two rules make those numbers true of every producer rather than only of the careful ones. Pieces are cut **before**
the sentence is built, and the ceiling for one whole sentence is applied where the text is stored or published: at the ring's
**single append funnel**, which holds the request path's failure
producer — it writes its own sentences, and interpolates a model id that came out of a request body — to the same numbers as
the engine's emitter; and where a ticket becomes **terminal**, which is the one place a failure turns into `ticket.Error`, the
exception the HTTP layer serialises, and the error frame of a stream that has already committed its `200`. The error writer caps
what it serialises as well, because the sentences it writes quote values a caller chose: a rejected profile name, an unknown
ticket id. These reporting caps apply before text is stored or published, including to provider exception messages.
For an upstream non-2xx response the router requests at most the first **256 bytes** from the content stream
and decodes only the returned bytes as UTF-8. It stops at that ceiling or an earlier EOF. An armed response
gate covers stream acquisition and the excerpt read. HTTP handler or socket read-ahead and response disposal
draining remain governed by the transport. The feed filter cuts its own values the same way, so a caller whose thread
id is longer than the reporting length still finds their records: reporting is bounded, lookup is not. What that buys is not
isolation. The `thread` and `engine` filters are **excerpt matching**: two filter values that share a head select the same
records, and a filter written literally as another thread's cut form selects that thread's records with no collision involved at
all. On a surface that is unauthenticated and loopback-or-behind-a-proxy (§6.1) that is an operator looking at the wrong thread,
never a way in — and it never hides the right one. Isolation on the feed is the `ticket` filter, and that one stays **exact**.

The admin ticket snapshot returns the router-generated `Id` and the full `ThreadId`, `Model` and, when assigned,
`Engine`. The ticket ID remains the exact correlation key. For HTTP inference, `ThreadId` may come from the
`X-Inference-Thread` header, independently of the request-body limit. The model comes from the requested model,
thread affinity or configured default after canonical resolution. A catalog alias can resolve to a different full
name, and library callers can supply identities outside HTTP. The engine name comes from configuration. The
serialized snapshot includes the full identities and JSON escaping, so `max_request_body_bytes` limits ingress
bodies and supplies no snapshot byte ceiling. The feed still uses reporting excerpts and the snapshot's `Error`
keeps the terminal reason cap.

- `thread.registered`, `thread.priority_changed`
- `request.enqueued {thread, ticket, model, priority, promptChars}`
- `request.selected {ticket, engine, model, affinityScore, busyScore}`
- `request.streaming_first_token {ticket, engine, msFromEnqueue}`
- `request.completed {ticket, engine, tokens, msFromEnqueue, msFromFirstToken}`
- `request.failed {ticket, reason}` — includes queue expiry, which has ticket error code
  `deadline_exceeded`
- `request.cancelled {ticket, reason}`
- `request.cleanup_failed {ticket, thread, engine, model, reason}` — offline admission cleanup fault,
  reported independently of the ticket's terminal result
- `engine.health {engine, healthy, loadedModels}`
- `engine.switch_started/completed/failed {engine, model, profile, ms}`
- `queue.rejected {thread, ticket, model, reason}` — refused before joining a queue; `reason` is `queue_overflow` or
  `model_unavailable`, and no `request.enqueued` event precedes it

The operator dashboard reads current state from `/admin/status` and uses `/admin/tickets/{id}`
for a known ticket. The event feed supplies retained observations. Label feed-derived counts
with the retained slice observed. Snapshot counters keep their own scope (§6).

TASK-11 tracks the richer ticket fields, event coverage and status rates proposed for expanded
views, along with a feed heartbeat, a bounded recent-request journal and deliberate admin
schema and casing work (§15.1). The current feed supports filtering and reconnect with the
exclusive cursor described above.

## 11. CLI

```
air completion bash|powershell|zsh|fish
air serve [--config f.toml] [--listen url]
air chat [--server url] [--thread id] [--priority high:200] [--model name] [--system text] [--no-stream] [--json] [prompt ...]
air complete [--server url] [--thread id] [--priority high:200] [--model name] [--system text] [--no-stream] [--json] [prompt ...]
air models [--server url]
air switch --engine e --model m [--server url]
air status [--server url]
air events [--server url] [--since seq]
air threads --id id [--priority high:200] [--server url]
air tickets id [--server url]
air config [--config f.toml]
air tune compare --baseline FILE --candidate FILE
air tune plan [--config FILE] --engine ENGINE --model MODEL --seed HEX --max-concurrency N [--profile TIER]
```

`air completion SHELL` writes the shell registration script to stdout. Its read-only backend
accepts the current word and preceding arguments as separate strings and emits one candidate per
line. The command and option vocabulary is available offline. Engine/model candidates include
cold configured entries and aliases, scoped to an engine when one precedes the cursor.
Offline tuning uses its explicit `--config` or the usual config lookup. Other commands consult
the local configuration or a bounded read of the explicitly addressed listener’s `/admin/catalog`;
an unavailable listener falls back to the local configuration. With neither source available,
dynamic lists are empty. Catalog text is completion data and is never executed as a shell command.

The global `--json` flag can precede the verb or appear among its options. Models, status, tickets,
switch acknowledgments and events render as text by default. In JSON mode their HTTP objects retain
wire field names and values, with one line terminator after each body or event. An HTTP refusal
before the event stream starts prints the error in the selected format and exits 1. Human status
labels an unreported window and the configured fallback for unknown slots. Switch acknowledgments
omit the legacy HTTP-status prefix in JSON mode. Thread registration emits a `ThreadId` object for its
empty HTTP 204 response. Config emits a resolved configuration summary and tune compare emits its text
report in the JSON string field `Report`. Command usage and local validation diagnostics remain on
stderr. Help, the serve host, the tune plan manifest and the completion protocol retain their formats.

`air serve`, `air config`, `air tune plan` and the standalone server use the explicit
configuration path first, then `ARBOREAL_ROUTER_CONFIG`, then `AIR_CONFIG`. With all
three omitted they open `~/arboreal/inference-router/router.toml` in the user's home directory.

`air serve` starts the router host in the CLI process. Client verbs construct HTTP requests and
read the router's responses directly.

`air chat` and its alias `air complete` send `POST /v1/chat/completions`. Use `-s` or `--server`
for the base URL. `-m` or `--model` supplies the JSON `model` when nonempty. `-t` or `--thread`
sets `X-Inference-Thread`, which defaults to `anon`. `-p` or `--priority` sets the optional
`X-Inference-Priority` header. The request streams by default. `--no-stream` sets `stream` to
`false`, and `--json` controls output rendering.

Options can appear before, between or after positional words. Value options take a separate
following argument with no leading dash. The first eligible value supplies each option, and
the parser consumes every eligible operand, including repeated ones. The CLI joins the remaining
positional arguments with single spaces. If none remain, it reads stdin to EOF and trims leading
and trailing whitespace. The resulting prompt fills one `user` message. A nonempty `--system`
value fills a separate `system` message before it.

A streamed response exits 0 after `data: [DONE]` if it carried no error (§6).
In text mode stdout contains the concatenated text deltas and a trailing newline. Stderr prints
status changes from router keepalive comments and any in-band error payload. With `--json`, stdout
contains the consecutive non-sentinel data payloads, including any in-band error, with no added
separators or newline. JSON mode ignores progress comments.

An in-band error exits 1 even when the sentinel follows. Orderly HTTP EOF before the sentinel also
exits 1 and keeps all partial stdout. The CLI writes `error: completion stream ended before [DONE]`
once to stderr if the stream supplied no in-band error. A non-success HTTP status exits 1 with
`error STATUS: BODY` on stderr. With `--no-stream`, a successful reply is printed as extracted text
or the original JSON body and exits 0.

`config` parses and validates the configuration offline. It prints each engine's resolved
`lifecycle_owner` and provider (`docker`, `exec` or `none`) alongside its model catalog and
serving profiles. It opens no sockets and runs no lifecycle commands. `status` reports each
engine's active profile, context window and slot count alongside health, loaded models and
in-flight count.

### 11.1 Offline tuning comparison

`air tune compare` takes two operator-authored `near-window-text-v1` summaries via exactly
one `--baseline` and one `--candidate`, in either order. It reads regular files on Linux x64
with glibc, kernel `openat2` support and trusted procfs. It supports ext2/3/4, XFS, Btrfs,
tmpfs and overlayfs by descriptor filesystem type. The operator chooses the storage.
The command pins and checks each file before reopening its owned descriptor via procfs.
Supported inputs have one hard link and direct paths without symlink components. The
command verifies its native file access facilities before admitting either input.

Each side is one case-sensitive UTF-8 JSON object of at most 65,536 bytes and depth 10.
Members are required except where the tagged value or row shape omits them. Unknown or
duplicate members, nulls, comments, trailing commas and a leading BOM are invalid.
All strings are bounded ASCII tokens of at most 128 characters. The first character is
alphanumeric and subsequent characters may also be `.`, `_`, `:`, `@`, `/`, `+` or `-`.
Digests are lowercase 64-character SHA-256 hex. The seed uses the same hex syntax.
IDs are redacted lowercase 32-character hex. Integer values use unsigned decimal syntax
and fit `Int64`, or `Int32` for schema version, slots, cap and concurrency.

Facts use `{"kind":"known","value":...}` or `{"kind":"unknown"}`. Limits use
`{"kind":"finite","value":...}`, `{"kind":"unlimited"}` or `{"kind":"unknown"}`.
The `value` member is present only for `known` and `finite`. Equal known unlimited limits
can match. An unknown matching fact withholds a delta even when both sides are unknown.

The root contains `schema_version` (1), `run_id` (ID), `instance_id` (ID fact), the `hardware`,
`target` and `workload` objects, `cap` (1–8) and `levels` (exactly `cap` rows).

| Object | Version 1 members |
| --- | --- |
| `hardware` | Byte-count facts `physical_ram_bytes` (positive when known) and `physical_swap_bytes`. Byte-count limits `ram_limit_bytes`, `ram_plus_swap_limit_bytes`, and `swap_only_limit_bytes`. Token facts `limit_source`, `cpu_model`, `os`, `driver`, `power`, `thermals`, and `background`. Digest fact `cpu_affinity_sha256`. `cpu_quota` is a limit with positive integer `quota_us` and `period_us` when finite. `gpus` is a fact containing 0–16 ordered entries when known, each with a unique token `id` and a positive byte-count fact `vram_bytes`. |
| `target` | Token facts `model`, `tier`, `quantization`, and `runtime_build`. Digest facts `weights_sha256`, `executable_sha256`, `tokenizer_sha256`, `template_sha256`, `recipe_sha256`, `shared_settings_sha256`, `cache_policy_sha256`, `deadline_policy_sha256`, and `adapter_sha256`. Positive integer facts `nominal_context`, `observed_context`, `usable_context` and `configured_slots`. Nonnegative integer fact `observed_slots`. |
| `workload` | Literal `id` of `near-window-text-v1`, token fact `generator`, and digest facts `generator_sha256`, `seed`, `warmup_corpus_sha256` and `warmup_body_sha256`. |
| Level | `concurrency`, `status` and digest facts `corpus_sha256` and `body_sha256`. Each `passed` row includes `wave_output_tokens_per_second`, exactly three positive decimal rates with up to nine integral and nine fractional digits and no exponent. A `failed`, `inconclusive` or `untested` row instead includes `reason`, one of `preflight_refusal`, `prior_level_did_not_pass`, `budget_exhausted`, `workload_failure`, `insufficient_evidence` or `other`. |

Each record starts at C=1 and proceeds in order through its cap. A passing prefix can end
in one failed or inconclusive row followed by untested rows. All rows can be untested if
preflight refused. Untested rows have unknown fingerprints. The reason
`prior_level_did_not_pass` applies only to an untested row immediately after a nonpassing
level. A cap must fit each known
positive slot count. A zero observed slot count requires all rows to be untested. Known
usable context must fit the known observed window. Levels above a side’s cap are shown as
outside cap.

Hash each generated user-text payload as exact UTF-8 before template rendering or JSON
framing. Hash each final request body as sent. The two ordered fingerprints hash ASCII
headers `air-tune/1/near-window-text-v1/corpus` and `air-tune/1/near-window-text-v1/body`,
respectively, followed by the concurrency in canonical decimal and then the corresponding
`3*C` leaf hashes in wave-major, worker-major order. Terminate each line with LF. Warmup
uses `warmup-corpus` and `warmup-body` in those headers, one leaf hash and no level line.
`cpu_affinity_sha256` hashes sorted unique CPU IDs in canonical decimal, one per ASCII
LF-terminated line. Evidence and policy digests refer to exact retained, redacted evidence
bytes. Keep run and instance IDs outside launch recipe identity.

For each passing row the report uses the middle of the three rates, with the lowest
concurrency winning a peak tie. Report peaks independently. A candidate-minus-baseline
delta requires two passing rows at the same concurrency, matching level fingerprints,
and known equal target, workload, effective limits, CPU and ordered GPU facts. Physical
memory, run/instance IDs, limit source and recorded environmental changes provide
context alongside the comparison. All results are unverified, operator-authored
observations. The command supplies no measurement, live authority or profile changes.

The report is assembled and checked against a 16,384 UTF-8 byte ceiling before writing
stdout. Valid comparisons, including incomparable records, exit 0. File, platform and
schema errors exit 1 with a fixed error category and empty stdout. Usage errors exit 2.
Two synthetic example inputs exercise the contract. [Baseline](tests/Arboreal.InferenceRouter.Tests/AirTuneFixtures/baseline.json)
· [Candidate](tests/Arboreal.InferenceRouter.Tests/AirTuneFixtures/candidate.json)

### 11.2 Offline tuning workload manifests

`air tune plan [--config FILE] --engine ENGINE --model MODEL --seed HEX --max-concurrency N [--profile TIER]`
reads one AIR configuration with the file admission rules in §11.1. It takes each
required option exactly once, in any order, and accepts one optional configuration path and one optional profile. The seed
is 64 lowercase hexadecimal characters and the cap is a single digit from 1 through 8.
The command selects JSON for a case-insensitive `.json` extension and TOML otherwise.
The admitted config must contain at most 65,536 bytes of valid UTF-8 without a BOM.
Only the admitted bytes go to the config loader. The planner uses the parsed catalog
without constructing an engine runtime or opening referenced files.

Select one exact engine key, refusing any case-insensitive twin of that key. Match
the model key and its aliases case-insensitively within that engine, requiring one
distinct canonical match. Select an explicit tier by its exact canonical name. With
no selection, sort all serving tiers by configured context, then by ordinal name.
Refuse absent ladders and duplicate tier names. The omission path accepts at most
16 tiers. An exact single-tier selection can use a longer ladder. The declared
default is descriptive. Tier names, contexts and slots come from the parsed
serving ladder. Name-only legacy profiles and Docker target aliases do not supply
alternate planner tiers.

The output is one compact JSON object in UTF-8 with a final LF. The root fields
in order are `kind` (`air_tune_workload_plan`), `schema_version` (1), `workload`
(`near-window-text-v1`), `generator`, `generator_sha256`, `engine`, `model`,
`default_profile` (null if undeclared), `seed`, `max_concurrency`, `policy` and `tiers`.
Only the canonical engine, model and tier spellings appear. They use the bounded
ASCII token syntax in §11.1. The generator identity is `air-tune/text-generator/v1`
and its hash names the exact embedded definition bytes.
[Generator definition](src/Arboreal.InferenceRouter.Cli/AirTuneTextGenerator.v1.txt)

| Object | Fields in order |
| --- | --- |
| `policy` | `fit_policy` (`near-window-fit-policy-v1`), `target_output_tokens` (512), `waves_per_level` (3), `request_seconds` (600), `output_idle_seconds` (60), `tier_seconds` (5400), `abort_drain_reserve_seconds` (30), `max_prefix_count_calls` (16), `max_tail_count_calls` (8), `max_count_calls` (24), `max_count_input_bytes_per_prompt` (8388608), `max_count_body_bytes_per_call` (349525), `max_candidate_bytes` (8388608). |
| Tier | `profile`, `configured_context`, `configured_slots`, `planned_cap`, `nominal_prompt_min`, `nominal_prompt_max`, `requests`. |
| Request | `kind` (`warmup` or `grid`), `concurrency`, `wave`, `worker`, `nonce` (64 lowercase hexadecimal characters). |

For configured context W, the inclusive nominal prompt band is [W−671, W−640].
W must be at least 672. The cap is the smaller of the operator cap and configured
slots. A tier begins with an unmeasured warmup at C=1, wave 0, worker 1. It then
lists consecutive C from 1 through the cap, with waves 1–3 and workers 1–C in
that order. At cap 8 it lists 109 coordinates including the warmup. The generator
uses a derived warmup seed and distinct nonce and record domains. Each coordinate
reproduces candidate text with the nonce first, a selectable prefix of complete
records and an optional byte prefix of the next record. There is no selected
count or tail in the manifest.

The policy and coordinates describe planned work. Measurements require a verified
resident, measured window and slots, real token counting and template rendering,
fitted prompts, and native and terminal evidence. Final corpus and request-body
hashes belong to that measured work and its saved summaries. The planner reads
no credentials, opens no backend connection, and emits no config file paths,
endpoints, probe paths, launcher settings or runtime observations. It changes
no configuration, profile or default. Validate the entire output including LF
against a 524,288-byte limit before writing stdout. Success exits 0. Input,
platform and planning errors exit 1 with fixed redacted stderr and empty stdout.
Usage errors exit 2.

## 12. Future architecture commitments

### 12.1 Router host + listener
`RouterHost` owns engines/queues/events; **listener** = stateless proxy with
the same `/v1` + `/admin` surface forwarding to a host over
`POST /admin/queue` + SSE backhaul. The CLI (`serve`) can: attach to an
existing host (`--host`), start a local host (`--local`), or start the
listener (`--listen-only`). v1 keeps the interfaces (`IInferenceRouter`,
event feed) host-agnostic; the wire contract lands when host mode lands.

### 12.2 Cross-host prefix prioritization
Scheduler gains a peer-level trie: prefer the peer whose trie already holds
the longest prefix match, before load balancing (mirrors §5.4 step 4 at host
granularity).

Prior art: SGLang's router uses longest-prefix affinity with a **match floor**
(default ≈1,024 matched tokens) and a `balanced` mode that compares the warm worker against
a power-of-two load candidate, breaking affinity only when *both* a relative (~2×) and an
absolute overload threshold fire. Preble/E2 formalizes it as *exploit the cached owner only
when matched-prefix tokens exceed the uncached remainder, else explore* using recent
prefill/decode work, and puts quotas on cache-hit routing to prevent starvation. Adopted
shape: keep stickiness only above a provisional gate (≥1,024 matched tokens **or** ≥20% of
the prompt), and break it with SGLang-style hysteresis rather than a raw score tie.

### 12.3 Global impact-aware scheduling
`SchedulerView.Engines` gains `remotePrefillCost` (uncached prefill stalls
others) — the β term in §5.4 becomes per-engine, learned from observed TTFT
regressions (EWMA), no interface change.

Prior art: production routers do not agree on one "busy" scalar, and request count is
the worst of the candidates. SGLang ranks prefill workers by estimated prefill queue time,
then waiting *uncached* tokens, then waiting/running/in-flight; decode workers weight
waiting/running + KV usage; NVIDIA Dynamo's KV router scores active prefill + incoming
uncached prefill + active decode KV blocks − a tunable cache-overlap credit that **decays
when the owner is busy**. Target score shape:

`score_i = queued_prefill_ms_i + prefill_ms(prompt_tokens − reusable_prefix_i) + λ·added_decode_stall_ms_i + μ·eviction_recompute_ms_i`

with λ/μ calibrated empirically. v1's `Inflight*32 + InflightTokenProxy*α + prefill_miss`
is the deliberately crude stand-in for that expression. Also from that prior art: Sarathi-Serve-style
chunked prefills mean a router should penalize long uncached prefills on engines that cannot
chunk, and vLLM may *preempt and recompute* when KV runs out — penalize that risk (the μ
term) instead of assuming preemption is free. Telemetry to expose later: queue age, queued
miss tokens, active decoding sequences/blocks, KV free space, preemption/eviction counts,
observed prefill ms/token and token-step latency.

### 12.4 Suffix-cache scheduling
Capability-gated scorer (§5.4); engines advertise `suffix_cache` + extent
query; scheduler prefers reuse-capable engines with matching suffix extents
subject to the fork's ordering constraints (unspecified here, by design).

Prior art — the constraint that keeps this capability-gated: in a causal transformer,
identical suffix *text* does not imply identical KV, because the suffix's hidden states
depend on earlier tokens. RoPE repositioning (Meta's suffix-cache-reuse work; llama.cpp's
`--cache-reuse` KV shifting) fixes positions, not changed contextual dependencies, so these
forks retain stale states and **quality must be benchmarked explicitly**, and fallback to
recompute when private cache slots are unavailable. CacheBlend shows non-prefix RAG chunks
can be reused by selectively recomputing context-sensitive token states rather than
concatenating KV. Router-side bookkeeping required: valid reusable spans, cache
owner/session, lifetime, relocation and repair cost — and never credit overlapping
prefix/suffix tokens twice.

### 12.5 Blazor web client
The Blazor live view is a read-only operator dashboard for current router state and retained
observations. Bootstrap engine gauges and queues from `GET /admin/status` and read a known
ticket through `GET /admin/tickets/{id}`. Use `GET /admin/events` and the ticket-scoped feed
for retained history and the live tail. Refresh current snapshots after a retention gap or
uncertain continuity. Scope event-derived counts to the retained observations shown (§10).
Use the loopback and tunnel deployment in §6.1.

Static SSR + selective Interactive Server per T011 guidance. The view uses the public API.

Screens (our shape, not an appliance's): global inference activity · queues and
admission · engines/profiles/switches · topology and trends · request timeline ·
health, config and security. From the comparable NVIDIA router (Personal-AI-Router),
**borrow** the workload rail, filters, topology view, endpoint copy and actionable
error presentation; **change** capacity/profile/readiness presentation to be
engine-first rather than model-first, and add queue/TTFT/throughput trends;
**skip** the installer, model deletion, pairing flow, autostart/update/wipe controls.
TASK-11 tracks the additional feed and schema work in §10 for expanded views.

## 13. Pi integration (session-scoped model switching)

Pi (the coding agent harness) is a first-class client of AIR. This section records
(13.1) what we require from the Pi side, (13.2) the design that satisfies it (a
design pass rooted in the current Pi extension API and a dated Pi fork snapshot —
unverified external analysis), and (13.3) what AIR must expose to make it work.

### 13.1 Requirements (what we want out of the Pi extension)

| # | Requirement | Verifiable when |
|---|---|---|
| P1 | Pi treats AIR as one OpenAI-compatible provider; no per-engine config in Pi | `pi --provider air --model <id>` streams a completion |
| P2 | Mid-session model switching keeps the transcript/session (`/model air/<id>`, `pi.setModel`, or an `air_switch_model` tool) | switching models mid-conversation does not start a new session or lose history |
| P3 | Model loading of 1–15 min never produces a spurious timeout in Pi | a cold model load surfaces as *waiting* state, then a normal completion |
| P4 | The extension shows why Pi is waiting (queued position, engine, currently loaded model, and for escalation: the context-window tier being loaded) | status line, e.g. `Waiting for strata/coder — 2 ahead; serving qwen-flash` |
| P5 | Context-window escalation (§7.6) is a first-class wait reason, not an error: a session whose context outgrows the active profile queues behind a profile switch and Pi keeps waiting patiently | a request over the active `n_ctx` escalates profile and completes |
| P6 | Cancellation propagates: aborting in Pi cancels the ticket, and a model load with no remaining claimant is abandoned | abort during queue/switch leaves no orphaned engine work |
| P7 | Catalog is live: `/v1/models` drives Pi's model list, with availability + loaded/switching state | `refreshModels` reflects an engine that just loaded a model |
| P8 | Thread identity + priority ride every request (Pi session → AIR chat thread) | admin `/admin/threads` shows the Pi thread and its priority |
| P9 | Failure modes are distinguishable and actionable (model unavailable, switch failed, engine died, ticket expired) | error `code`s are stable, terminal ones do not retry-storm |
| P10 | No Pi core patch required: built on the shipped extension API only | extension loads against current pi + the vendored fork package |

### 13.2 Design (research-pass disposition)

- **Shape**: a **native Pi provider extension** (not a models.json entry alone).
  `models.json` carries URL/key, metadata overrides, and emergency static fallback;
  the extension owns discovery, thread identity, switching, waiting, cancellation, and UI.
  Suggested layout: `.pi/extensions/air/{index,provider,air-client,wait-registry,types}.ts`.
- **Provider wrapper**: `streamSimple` performs the AIR control-plane work, then delegates
  the *ready* request to Pi's built-in `openai-completions` stream. It returns immediately
  but emits Pi's `start` only after readiness, so preparation failures become precise
  pre-stream `error` events instead of post-200 breakage.
- **Two-phase wait (the answer to P3/P4/P5)** — never hold a chat completion open while a
  model loads:
  1. `POST /admin/switch {thread, engine, model, profile, priority, requestId}` →
     `200 {ticket, state:"ready", seq, leaseExpiresAt}` or `202 {ticket, state:"queued"|"switching", seq}`.
  2. On 202: `GET /admin/events?ticket=<id>&after=<seq>` — replayable SSE (`id`,
     `event: ticket`, JSON `{state, model, queuePosition, message}`) with `: keepalive`
     every ~15 s; reconnect with the last sequence; after 3 failures fall back to polling
     `GET /admin/status?ticket=<id>` every 5 s.
  3. At `ready`, call streaming `/v1/chat/completions` carrying the ticket; AIR atomically
     validates the short reservation and emits normal OpenAI chunks.
  State machine: `IDLE → PREPARING → QUEUED|SWITCHING → READY → STREAMING → DONE`.
- **Headers**: `X-Inference-Thread: pi:<sessionId>`, `X-Inference-Priority`,
  `X-Inference-Request`, and `X-Inference-Ticket` once ready.
- **Switch semantics**: switching applies to the *next* provider call; neither the fork nor
  current Pi replaces an in-flight completion (P2 constraint).
- **Anti-thrash (server-side, visible to Pi)**: schedule **model cohorts** — never preempt
  active generation, drain the loaded-model cohort, enforce minimum residency/hysteresis,
  then switch once to the highest effective-priority/oldest cohort. Thread stickiness
  remembers a target without reserving the GPU.
- **Errors**: `{"error":{"type":"air_error","code":"model_unavailable|switch_failed|engine_died|ticket_expired","message":"…"}}`;
  terminal codes fail once, `ticket_not_ready` re-enters the wait exactly once.
- **Rejected in that pass** (kept as fallbacks only): SSE comment frames on a *held* completion
  (defeats `httpIdleTimeoutMs` but not the OpenAI SDK's `retry.provider.timeoutMs`, and a
  post-200 failure is awkward); returning 202 from `/v1/chat/completions` (not a stock
  completion, and Pi does not retry 202); long-holding non-streaming calls (cap briefly,
  then return a standard OpenAI error). Optional in-band status frames must be no-op
  `chat.completion.chunk` objects with `choices: []` — never text deltas.
- **Fork note**: the dated Pi fork snapshot reviewed here (2026-09-06) already has
  `registerProvider`, `refreshModels`, `before_provider_headers`, `pi.setModel`, and UI
  status hooks; it lacks `provider_stream_event`, so do not plan to parse control chunks
  through that hook.

### 13.3 AIR surface still needed (delta from what ships today)

Implemented: `/v1/chat/completions|/v1/completions|/v1/models` passthrough, SSE ticket
stream with keepalive frames, `/admin/{status,events,threads,switch,tickets/{id}}`
(GET + DELETE cancel), ticket snapshots, `engine.switch_*` events, `switching:{engine,
model,eta}` in stats, `X-Inference-Required-Context` (declared context demand), and
`/admin/switch` accepting `profile` (echoed in the ack, window-checked in the
wait-for-ready loop).

Shipped since: ticket-filtered replay — `GET /admin/tickets/{id}/events` and `ticket=` on
`/admin/events` — and `X-Inference-Ticket` on every admitted response (§6).

Still to add for the Pi extension (P1–P10):
`/admin/switch` accepting `priority` + `requestId` and returning the ticket
with `seq`/`leaseExpiresAt`; ready-ticket validation at inference time (atomic lease
consume); `X-Inference-Request` / `X-Inference-Ticket` header plumbing; `POST
/admin/tickets/{id}/cancel` alias (today cancellation is `DELETE`); `/v1/models`
enriched with `air:{engine,profile,availability,loaded}`; the stable `air_error` codes;
and cohort/residency hysteresis in the scheduler (§12.3 hook).

## 14. Testing & acceptance

- **Unit**: priority comparability, queue ordering/overflow, scheduler
  scoring (affinity vs busy), serving-profile escalation decisions (fit, smallest
  tier, unreported window, ladder cannot fit, `auto_switch=false`), TOML binding
  (valid/invalid/unknown kinds, serving ladder + `default_profile` validation,
  shipped samples load as written), lifecycle argv quoting (incl. a round trip
  through a real shell), probe gauge path resolution, prefix trie match/eviction,
  DTO round-trips.
- **Integration** (in-memory fake engine serving recorded OpenAI SSE):
  end-to-end enqueue → select → stream → complete; cancellation;
  switch-while-queued (request waits for model load); context-window escalation
  (small request served, oversized request queues behind a profile switch that
  widens the engine's reported window, then completes); health probe failure;
  real HTTP through Kestrel against the stub engine.
- **Configuration reload** (`ConfigReloadWireTests`, `ConfigReloadRaceTests`): authenticated
  file apply, generation readback and rejection, exact stream bytes and a held native switch,
  queue and shutdown ordering, idle external retirement, detached response disposal, and
  retained external reported-default validation.
- **Benchmark** (`bench/`, not part of the test suite): overhead ladder against a shared
  fake engine; the gate for any §9.1 claim and the regression net for streaming handoff
  stalls. See `docs/benchmarks.md`.
- **AOT**: full AOT publish of Server + Cli with zero trim warnings on CI.
- **Binary smoke test** (`tools/ci/smoke.sh` against `tools/ci/fake-upstream.py` and
  `samples/router.ci-smoke.toml`): a published binary must validate a config without opening
  sockets, serve the OpenAI-compatible surface against a fake engine, relay every streamed frame
  (the §6 handoff), and map an engine failure to `502 engine_error`. Runs on the machine whose
  architecture the binary targets, before any release exists.
- **CI** (`.github/workflows/ci.yml`): restore, build with warnings-as-errors, the full test
  suite, then a Native-AOT publish (Cli, which carries the Server) that must emit no trim/AOT
  warnings — on every push to `main` and every pull request. Tests are POSIX-aware today, so CI
  runs on Linux; each platform binary is still exercised natively at release time.
- **Published surface** (`.github/workflows/release.yml`): `linux-x64` and `win-x64`, each built and
  smoke-tested on its own runner, gated before publish. Each leg artifact keeps its own download folder,
  matched by exact leg name: an artifact named for no declared leg (or for two legs at once), a leg folder
  holding any entry other than the two that leg produces — a directory or a symlink included — and a
  basename arriving from two artifacts are all refused. Every archive and sidecar must be present, a regular
  non-symlink file, and non-empty; the archive must unpack and hold exactly one member, a regular file
  whose *type* is checked and not only its name, named literally `air` / `air.exe`; and it must match the
  whole checksum record its own leg wrote — digest and filename together — rather than a name read out of
  the sidecar. The upload list is built from the same
  `EXPECTED_RIDS` list the gate reads, so a leg cannot be verified by one set of names and published by
  another. The release is created by `POST /releases` as a draft, and the id that response handed this run
  is the id used for every later call — asset uploads, inspection, publication, cleanup; nothing looks a
  draft up by tag, because the tag endpoint answers published releases only. The address the assets are
  posted to is **built by this run** from repository context and that validated, digits-only id rather than
  taken out of the response, and the response's `upload_url` is required to equal exactly that address, so a
  server-supplied string never decides where the binaries go and a template GitHub no longer uses stops the
  run instead of rerouting it.
  That response is read as a whole — its tag, draft and prerelease fields must match what was asked for,
  and its `upload_url` must be exactly this repository's and that release id's documented address — folded
  for case only across scheme, host and owner/repo, which the API returns canonically and treats
  case-insensitively, and byte-exact from the release id on, so a differently-cased path segment or an
  expanded query is a refusal rather than a place to post binaries — before anything is uploaded; the
  comparison runs inside `jq` on the original JSON, never on a shell copy, because command substitution
  strips a trailing newline and an address that is not the documented one compared equal to it under
  v0.12.17; the id must also *print* as plain ASCII digits (`1.0e2` prints as `1.0E+2` and is refused —
  the previous revision posted five assets at that spelling before stopping, and what is validated here is
  jq's printed form rather than the wire spelling, which is the form this step interpolates). The
  release title is the tag itself, with GitHub-generated notes as its body. `target_commitish` is **not sent**,
  deliberately: the endpoint resolves an omitted target to the latest commit on the default branch, and the
  documented guard — the token must be authorized to modify workflows when the *resolved target* adds or
  modifies a file under `.github/workflows/` relative to the default branch, and the Actions `GITHUB_TOKEN`
  cannot be authorized for that, answering `404` and sometimes `403` — is evaluated against whatever target
  it resolves. Naming the tag commit would make the answer depend on how that commit differs from the default
  branch at the moment of the call, a comparison this run cannot evaluate before making it; leaving it unset
  resolves to whatever target GitHub picks for this tag. Whether that makes the guarded comparison empty is
  **not** a construction this run can lean on: the same endpoint documents `target_commitish` as unused when the
  tag already exists, so for a pre-existing tag the resolved target is GitHub's — the tag's commit, or the target
  the release already stores — and the direction of the comparison for a tag behind the default branch is not
  specified anywhere. The inference is recorded as an inference, the parameter stays omitted, and what the guard
  actually answers is canary evidence rather than a desk conclusion — and this repo's own tag commits do touch
  `release.yml`. Every such comparison is made
  against the JSON object with **typed** equality, and no response is ever flattened into a string and split
  again: `join(" ")` plus `read` collapses the gap a null field leaves and turns a space inside a string
  value into a column separator, so a `name` of `"v0.1.0 true"` with no `draft` field would satisfy a tag,
  title, draft and prerelease check at once. Every answer the gate acts on is also exactly **one** JSON document
  (`jq -s` with `length == 1`), because `jq -e` reports the status of its last result and a two-document answer
  is otherwise judged only by its last document. Response text reaches the log only after it is reduced to
  printable ASCII, cut at 200 characters after that reduction, and escaped for `%` and `:`: the runner scans
  stdout for workflow commands, and a control character, an ANSI escape, a bidi override or a split UTF-8
  sequence from a response has no business in a log line either.
  Preflight and both publication rechecks resolve the full `refs/tags/<tag>` reference. Before
  publication it must still name the commit the legs built, and the
  draft is inspected on the remote from one list of that release's own assets, every entry of which is
  required to be well-formed before any of it is parsed — the length equal to the expected count, so a list
  cut off by pagination is not an inventory that passes, with a declared set of 100 or more assets refused
  before anything is created because one page of 100 would stop being complete; each name drawn only from the
  characters this gate's own asset names use and matched with `\A…\z` rather than `^…$`, since `$` in
  Oniguruma also matches before a final newline and an asset named `"air-win-x64.tar.gz\n"` would split one
  snapshot row into two; `state` and `size` typed, `digest` absent or a sha256, and ids canonical integers below
  2^53 — because the asset name
  is the field boundary of the line each entry is read from, and an asset named
  `"<wanted> uploaded <size> <digest>"` would otherwise carry its own state, size and digest into the
  columns compared. Then: asset inventory, each
  asset's upload state, byte size and stored-bytes digest against the staged file (an asset whose digest
  the remote does not report fails the gate, and an asset name appearing more than once fails it), and
  the release object's own tag, title and draft/prerelease flags as fields of the object read back by id,
  with `draft` required to be the boolean the API documents rather than the string `"true"`; that
  inspection runs again, tag re-resolved, immediately before publication, and publication is a REST update
  of that same id whose own response is checked the same way before the confirmation read-back — and a
  failure there says the release may already be public, because a `PATCH` that was sent is not a run that
  published nothing. The record the run prints of what went public is built from the values it verified, not
  copied from the response; everything else the step writes — asset names and ids it verified, field text it did
  not — reaches the log through the sanitizer above. An earlier §14 sentence claimed no server-chosen text
  reaches stdout at all, and that is not true of the diagnostics; the corrected claim is the one here.
  A failed run deletes only
  that captured id, and only from a response whose numeric id, tag and boolean `draft` field are all
  verified; a run that captured no id
  deletes nothing, and one that attempted a create without capturing an id says so as a
  manual-intervention annotation instead of finishing quietly. The step before the build refuses when a release
  already carries the tag, and that refusal covers a release list it cannot read as well: "could not tell" is
  not "does not exist". The reason this SPEC gave for that rule was wrong and is corrected here: a second
  `POST /releases` for a tag that already has a **published** release is refused by GitHub —
  `422 {"resource":"Release","code":"already_exists","field":"tag_name"}` — measured in a disposable
  repository, not inferred (whether a *draft* on the same tag is refused the same way was not probed). The rule
  still stands for the reason that survives: this step judges existence from a **list** read, and that read was
  observed three times in a row to omit a release created moments earlier, so a page that does not show a
  release is not evidence of absence. macOS RIDs stay declared in the Cli project so a local
  `-r osx-arm64` build works, but no macOS artifact is built, published, claimed or gated: the account has
  no macOS runner minutes, and a leg that queues forever is not a leg that passes. A platform is added
  back with runner minutes, a leg that actually runs, `EXPECTED_RIDS` updated, and README copy that
  follows it. **The gate has now run for real once**: tag `v0.0.10-canary` at the commit the legs built, all
  four jobs green, five assets in state `uploaded`, prerelease, `immutable: true`, and the digests read back
  over HTTPS from the served bytes equal to the per-archive records and to `SHA256SUMS`. So the gate is
  configured **and** proven for one tag; it is not proven against an attacker, and the two repository controls
  around it are now measured rather than assumed, from a disposable repository created and archived for the
  purpose:
  - **The tag ruleset is the only measured protection of the tag ref.** With `update` + `deletion` and
    `bypass_actors: []` on `refs/tags/v*`, both a repository-admin token and the Actions token are refused
    (`Cannot update this protected ref.` / `Cannot delete this tag` / `GH013 … push declined due to repository
    rule violations`), including a workflow running with `contents: write`. A ruleset created **without**
    `conditions.ref_name` matches nothing and protects nothing — that was measured the hard way — and the same
    shape leaves non-matching tag names untouched.
  - **A `creation` rule would break this pipeline**, so the shipped ruleset omits it: with a creation rule
    present, the Actions token's brand-new `v*` push is refused, an owner-token push is refused, and the release
    API's implicit tag creation is refused (`pre_receive … Cannot create ref due to creations being
    restricted`). With the shipped shape, the Actions token creates its tag normally and still cannot move one.
  - **Immutable releases burn the version name and lock nothing else that was measured.** The name stays
    reserved after the release is deleted — `422 {"field":"tag_name","message":"tag_name was used by an
    immutable release"}` — and implicit ref creation is refused while creation is restricted. What is *not*
    locked, each on a published release with the repository flag on: assets (deleted `204`, then a **different
    file** re-uploaded under the same name, and the download URL serves the new bytes), release metadata
    (`PATCH` on name and body answers `200`), asset metadata (`PATCH` `200`), the release object itself
    (`DELETE 204`), and the tag ref (with no ruleset in place, a force-push moved the tag of a published
    immutable release). A release created while the flag is on reports `immutable: true`; a draft reports
    `false` until it is published; the repository flag is reversible (`DELETE` → `204`). The same-name upload
    collision (`422 ReleaseAsset already_exists`) is an ordinary name collision, not an immutability guarantee:
    an ordinary published release answers it identically. Consequence for this gate: **asset integrity rests on
    the run's own digest records and the post-publish readback, not on the immutable flag** — an operator token
    can rewrite a published release's assets — and the ruleset is what stops a buggy or captured workflow from
    moving the tag those assets are named after.
  The published `SHA256SUMS` and per-archive sidecars are a transfer and collection
  damage check, not provenance: there is no signing and no attestation.
- **Compat test**: `curl` with stock `openai` python client against the
  server (chat, stream, models) — must pass unchanged.

### 14.1 Robustness bar (parity review)

An external source review of this router against a mature local-inference router
(NVIDIA Personal-AI-Router, both at pinned commits) concluded: **we are not yet as robust,
even single-host**, while leading on priority, admission, serving profiles and prefix policy.
The bar it sets, in order:

1. **P0** — URI/path composition; readiness that is *proven* (health, context, capability)
rather than assumed; dispatch progress that cannot spin or starve.
2. **P0** — transport discipline: bounded bodies, per-phase timeouts, a commit boundary for
   in-flight work, cancellation that reaches the engine and releases capacity.
3. **P0** — switch serialization/coalescing with state reset on every failure path, and a
   stable, table-pinned error contract.
4. **P0** — owned lifecycle: drain, crash recovery, restart generations, terminal ingress
   (§5.3, the native lifecycle task).
5. **P0** — security: ingress auth/TLS/rate caps, engine keys, fail-closed resource identity.
6. **P1** — version/config pins, atomic reload, bounded telemetry with gap detection,
   durability of the request journal, fairness/starvation bound, then the live view (§12.5).
7. **P2** — multi-host (§12.1–§12.3), which is L/XL effort and stays speculative until 1–5 hold.

**Where the bar stands** (each line checked against the code and its tests, not against the review):

| Bar item | State | Where / what is still open |
|---|---|---|
| 1 URI + proven readiness + dispatch progress | **closed** | `EngineRuntime.Combine` one join rule (§7.5, `UriCompositionTests`); a switch is proven by health + loaded-model + window-fit probes and fails loudly with one actionable message (§5.3.1); dispatch and the feed relay are signal-driven (`_dispatchSignal`, `WaitToReadAsync`) — no polling loop, and the 200 ms TTFT poll is gone (v0.8.4); and a request naming a model nothing in the configuration can serve is refused before it joins a queue at all with 503 `model_unavailable`, on a decision taken against one coherent capability snapshot, so no queue position is occupied and no torn read can refuse a servable model (v0.12.25, v0.12.27). **Still open:** a legitimately queued request whose caller set no `max_queue_time` has no ceiling of any kind, and a request admitted while capability was unknown is not reclassified when absence becomes known |
| 2 transport discipline | **closed on the request side** | bounded bodies ✔ (`max_request_body_bytes`, 413 `request_body_too_large`, §6); cancellation reaches the engine ✔ (§5.5 wire tests); lifecycle phases are timed ✔ (`stop/wait/call_timeout_ms`, health `timeout_ms`); and every request-side wait now has its own ceiling ✔ (`switch_wait_timeout_ms` for a switch the request triggered — over the ceiling the request fails `switch_failed` with its slot handed back; per-engine `response_timeout_ms` for a turn at the engine — over the ceiling it fails `engine_timeout` 504 and the slot is released, or ends in band when the response had already started). `RequestPhaseTimeoutTests` pins all three outcomes, including that a caller's abort still reports `cancelled` and not a timeout, and that a switch survives the waiter that started it. **Still open:** a wedged *engine process* is noticed through health probes rather than through the request path (§5.3.2), a switch reserves an inference slot rather than its own lifecycle reservation (TASK-25), the HTTP relay limits are opt-in (§6 and §7). Enabling 262,144 bytes and 300,000 ms by default requires the representative 10,000-stream per-engine gap replay with zero false idle expiries and a real HTTP/1.1 and HTTP/2 stopped-reader soak (TASK-30). RequestPhaseTimeoutTests and StreamRelayTests pin the configured timeout and output paths offline |
| 3 switch serialization + error contract | **closed** | single-flight per engine with `_switchState` reset on every failure path (§5.3.1); error codes and casing pinned by `ErrorContractTests` against the §6.1 table |
| 4 owned lifecycle | **partly open** | provider seam + coordinator + Docker provider shipped (§5.3.1, §15); request-side phase bounds now exist (§5.5), so a select command that hangs no longer parks traffic indefinitely — the waiting request fails at `switch_wait_timeout_ms` while the switch runs on; **open:** a switch still occupies an inference slot instead of its own lifecycle reservation (TASK-25), and the §5.3.1 non-claims — image identity pinning, mounts/GPU, in-place model mutation, latched failures (TASK-26) |
| 5 security | **open by deployment posture** | General ingress authentication, TLS and rate limits remain v0.1 ingress work. The dashboard uses a loopback listener and operator-managed tunnel. The listener is configurable and the opt-in reload/reset checks are scoped to those routes (§6, §7.7). Engine keys remain per-engine configuration. |
| 6 P1 set | **open in order** | Bounded replay, an exclusive cursor, ending slow feeds and causal ordering are shipped (§10). The shared gap event was declined and the ring carries no epoch. Request-journal durability and the fairness/starvation bound remain open. |
| 7 multi-host | **not started, deliberately** | §12.1–§12.3 stay speculative until rows 1–5 hold; no task is cut for it yet |

Nothing in the open rows is a surprise to the design: each is either a task on the board or a row here
with the reason it waits. What the review *did* change is the order of work and the refusal to call the
router robust until rows 2, 4 and 6 close.

## 15. Research questions and dispositions

| Question | Disposition |
|---|---|
| Manual release preflight and exact tag resolution (TASK-90) | **Covered offline in v0.12.52.** The historical resolver accepted a same-named branch when the tag was absent. The current resolver reads manual input through the step environment, rejects a malformed full value before any lookup, and forces dry run. GitHub returned HTTP 422 for the short `tags/<tag>` lookup on the annotated canary and returned its peeled commit for `refs/tags/<tag>`. Initial resolution and both publish-time rechecks use the full tag reference. The harness models the 422 and pins manual dispatch, historical branch-only acceptance, tag push, and both publish lookups. `release gate harness (release.yml step bodies)` runs 137 cases with fake `gh`. The live gate run remains TASK-42. |
| Cysharp-style performance guidance for this codebase | **completed** — no prior packet existed; the result is the style digest in §9.1. |
| AOT-safe TOML: CsToml vs Tomlyn on .NET 11 | **completed, confirms shipped choice**: Tomlyn 2.11.1 ships a Roslyn source generator (`TomlSerializerContext`, `[TomlSerializable]`, `[TomlPropertyName]`) — what §9/`ConfigDto` use. CsToml + CsToml.Generator 1.8.6 (prozolic, *not* Cysharp) is the alternative with per-property annotations. |
| ASP.NET minimal APIs under Native AOT on .NET 11; STJ source-gen for streaming | completed; dispositioned in §9 |
| Prefix/suffix-aware scheduling prior art (SGLang router, Llumnix, vLLM) | **completed**; distilled into §12.2/§12.3/§12.4 |
| Pi mid-session model switching + wait protocol design | **completed**; folded into §13.2/§13.3 |
| Recommended Roslyn analyzer stack for code quality on .NET 11 AOT | **completed, integrated** (§9.2: central packages, banned-API policy, warnings-as-errors, 0-warning build + 0-warning AOT publish) |
| Tomlyn DOM vs source-generated binding API on .NET 11 | **completed**: keep Tomlyn 2.11.1 source-gen; untyped `TomlSerializer.Deserialize<TomlTable>` is reflection-free/AOT-safe if a DOM path is ever needed (not needed today) |
| AOT-safe Docker access from .NET 11 (client library vs raw engine API) | **Completed for the shipped transports.** The raw Engine API uses `SocketsHttpHandler.ConnectCallback` over `UnixDomainSocketEndPoint` or an HTTP(S) daemon base, with source-generated JSON DTOs. The provider pins the configured API version, demultiplexes exec frames, and distinguishes inspect 404 from transport failure. Windows named pipes are declined because the provider lacks a qualified Win32 pipe connector (§5.3.1). |
| Robustness and UI comparison against NVIDIA Personal-AI-Router | **completed**: verdict + ordering in §14.1, feed prerequisites in §10, UI borrow/change/skip in §12.5. Where that router leads on robustness we have tasks; where we lead (priority, admission, profiles, prefix, generic endpoints, engine keys, per-engine instances) is not a gap to close |
| Concrete Docker injection scope (TASK-128) | The startup check now compares engine, router ID and daemon against the same scope the injected provider uses for labels and transport. The focused fixtures cover independent mismatches, Unix and HTTP scope, caller ownership, matching switch readiness and external forwarding. The check covers configured identity. Host authority and physical isolation remain separate requirements. |
| Native engine-lifecycle design (transaction shape + container seam) | **completed**, adopted in §5.3.1: provider seam (`IEngineLifecycleProvider` over a coordinator), transaction order, single-flight per target with latched failures, ownership gate with no adoption of unowned resources, advisory journal, preflight as a validate-only verb, `select.exec` kept as a mutually-exclusive delegated provider. It preferred the docker CLI as the first Docker provider (smaller safety-critical surface); we keep **raw Engine HTTP** instead, because "no client library" from the review above is about reflection-rooted SDKs rather than `HttpClient` — the CLI stays as an operator fallback |

| Deep review of v0.12.4 (request-side phase timeouts) | **completed, dispositioned in v0.12.5**: the timer-stops-at-headers gap, the late-response leak, the switch running on the initiating request's token, and the switch-state wedge on `no_select_command` are fixed with tests; the stream channel is now closed by the ticket's terminal transition (which also fixes the silent stream end). Two findings are **accepted gaps**, not fixes: (a) the selector's own failure reason is recorded on the feed as `engine.switch_failed` and deliberately kept out of the caller-facing `switch_failed` message, so the caller sees the outcome and the operator sees the cause — change this when §13 needs to distinguish them programmatically (**moved in v0.12.33**: the caller now also gets a bounded reason *category*, which is what §13-style retry decisions need; the selector's own text is still kept out of the caller's body, so the half of this finding about provider text still stands); (b) a phase-timeout value above `Int32.MaxValue` ms is rejected at DTO binding (a Tomlyn/STJ range error) rather than by the router's own `RouterConfigException` message — either way startup fails with a non-zero exit, so the operator is stopped, only with a less specific line |

| Deep review of v0.12.5 (response gate, engine-owned switch token, stream-channel ownership) | **completed, dispositioned in v0.12.6**: fixed a false `Ready` — a waiter for one serving profile was answered by another profile's switch on the same model, so a request could be dispatched against a window it cannot serve (`SwitchFairnessTests` pins both directions: not ready on a stranger's success, not failed on a stranger's failure); fixed a shutdown race where disposing the switch token's source could throw before the switch task's own cleanup and strand every waiter on that engine (the token is read before the task is scheduled, the source is deliberately *not* disposed, and a switch that fails to start settles its waiters); the ceiling can no longer reassign blame for a failure inside the router's own terminal work, because the gate is passed the moment the owed bytes are in hand; stream acquisition sits inside the ceiling; a switch-failure message names the target that failed rather than whichever switch happened to be in flight. **Accepted deliberately**: a ceiling that loses the race with the engine's bytes does not fire (bytes in hand beat a timeout — the alternative is destroying an answer the caller can use), and there is **no idle bound after the first frame** — a stalled generation and a stalled reader are TASK-30, because each needs a wire decision (a truncation surface, a slow-consumer policy) rather than a constant |

| Commit review of the admission refusal (83ebc75: the capability check itself) | **completed, verdict REVISE, three of four findings adopted after checking each against the code**. (1) **Adopted**: requests admitted while capability was *unknown* (an engine whose `/models` probe had not answered) are never revisited once absence becomes known, so they keep a queue position and a head position with no ceiling. Reproduced by reading the code — nothing re-examines a queued ticket on a capability change — and the honest scope is that this is the pre-existing hang narrowed, not closed: the shipped rule classifies a ticket once, at admission. **Not closed by this commit**; it is the board's follow-up task, and it is why §5.5 now states the limit instead of implying completeness. (2) **Adopted**: the capability scan read `Healthy` and the reported list per engine at different instants, so a probe publication between two engine reads could make "every engine healthy, none can serve" true although it was true at no instant — a 503 for a model that was being served throughout. Fixed by making capability one immutable, versioned snapshot per engine (`EngineCapability`, written only through a CAS-publish) and by requiring the version sum to be unchanged across the scan, with a retry and then "not hopeless". (3) **Adopted**: the scheduler's view set `LoadedModels` and then asked `CanServeModel`, which re-read the same volatile array, so the bit and the list in one view could disagree. Both are now derived from one snapshot read. (4) **Adopted as documentation**: the shipped comment claimed the scheduler's wait reasons are diagnostics "for the feed" — `RejectReason` is read by the scheduler's own tests and reaches nothing else, and the wording is corrected rather than the code. **One finding of the later plan review checked and found false here**: that engine publications do not wake the dispatcher. They do — `RouterCore.WakeOnEngineState` subscribes to the event ring and signals on `engine.health` / `engine.profile` / `engine.switch_*` (`RouterCore.cs:63-68`), which is why no periodic pump was added and why the reconciliation work needs no new wake mechanism, only a decision at the head of a queue. **Also from the plan review and adopted**: a version bump that *follows* a data write is not atomic, so the sum-stamp argument is only sound with the version carried inside the snapshot (as shipped), and a switch marker cleared before the model appears in the reported list is the same torn-state failure — switch start and completion now publish the marker through the same snapshot, and switch success publishes the reported list *before* clearing the marker. **Precedence changed on the review's prompting**: the capability refusal is evaluated **before** the queue cap, so a full queue of a hopeless model can never answer 429 for a request that deserves 503; the shipped §5.5 says so and the event shape moved to `queue.rejected` with it. **Handed to the board rather than fixed here**: `_queues` keeps one entry per distinct model name a caller invents and there is no global queue-depth bound (`max_queue_per_model` is per model), so queue-key cardinality is a separate question. |
| Fourteenth deep review of the release gate (over the v0.12.22 gate: `806d10d`) and the first live canary run | **revise — three findings reproduced against this code before anything changed, one rejected.** Reproduced and fixed: numeric and boolean fields escaped every cap, so the declared `rsum_line` was not a bound and a long numeric field could hide the flags; `http_codes()` invented status codes out of HTTP version digits; `fdiff()` claimed a first difference for two equal strings and was slow on long ones. Accepted as written: the cut marker sat inside the surrounding quotes (the fragments are quoted and the marker sits between them, and a literal `+(9ch)+` inside a value is now distinguishable because values are quoted); and one `echo` of a response-derived value remained where the rest had become `printf`. Rejected: the claim that the harness cases were meaningless — they were narrow, and the fix is the cases the review named (a 40-digit id against the bound, both checksum record modes, an unreadable read), which are now in the file. **The live run found what no case could**: the Windows leg's checksum record is written in binary mode and the gate compared it byte for byte against a text-mode line, so the gate refused an archive whose digest was equal and the message blamed the digest. Both halves are now pinned — legs write `-b`, the gate compares the digest field and the name field separately and says which disagreed, and the harness writes the Windows form rather than only the Linux one that matched my assumption. |
| Thirteenth deep review of the release gate (over the v0.12.21 gate: `79a21e0` back to `8f78a73`) | **completed, dispositioned in v0.12.22; verdict REVISE, and both P1s reproduced here before adoption**. The first finding was against my own previous fix: the per-field caps build a line, and `brief()` then cut that **line** at 200 characters head-only, so a long tag and title could erase the end of `upload_url`. **Fixed**: `brief()` takes a bound, keeps the head and tail of a cut value with its true length, and escapes after cutting so `%25` and `%3A` stay intact. v0.12.22 set the summary limit to 340. v0.12.23 corrected fields that could bypass their own caps. **Regression proof corrected in v0.12.62:** the original `line_bound_tail` rendered 176 bytes and passed on both revisions. With a 41-character tag and matching title, `79a21e0` renders 216 bytes before its whole-summary cut. It still shows all six labels and the field markers but loses the address tail. The current step must keep the tail inside the field summary. A 198-byte historical control keeps the tail, and all three cases require the same stub call sequence through create refusal. `rsum()` renders **presence and type**: `<missing>` (field absent) vs `<null>` (present, null) vs `<object>`/`<array>` vs a JSON-quoted string with head and tail quoted separately so the elision marker sits outside the quotes — before this `{"draft":"false"}` and `draft=false` printed identically, as did `{}` and `{"id":"absent"}` (`type_presence`). Status extraction never matched `HTTP/2 500` (the version `2` sat in the `[^0-9]{0,4}` gap) while matching `mystatus=403`; the new pattern takes an HTTP version plus a status or a status-shaped label and refuses `HTTP/2 5000`, `500ms`, `4040` (`http2_status`, both directions). `printf '%s\n'` replaces `echo` at the six response-derived refusals, since `xpg_echo` turns a literal `\n` into a newline **after** sanitizing. And an address refusal reports the **first differing character** against the documented address (`fdiff_offset`, char 71), because two 124-character addresses differing in the middle render identically under head+tail truncation — printing both was never a comparison. **Rejected after checking**: a malformed-escape guard at the cut boundary (unnecessary: escape-after-cut), and marker-forgery hardening (the quoting now shows the marker inside the value, and the summary is a diagnostic, not a proof). **Accepted as limits**: NUL bytes dropped by command substitution, `jq`'s depth limit failing closed, `tr` expanding each non-ASCII byte to one `?` bounded by the outer cut. Harness 115 → 122 cases, 0 failures, now a CI job of its own over `tools/relgate.py`; it proves control flow and response reading only, and no tag has run the gate. |
| Twelfth deep review of the release gate (over the v0.12.20 gate: `8f78a73` back to `8f834f6`) | **completed, dispositioned in v0.12.21**. **Verdict taken**: the digits-only stderr is appropriate fail-closed hardening, but the per-field caps added one revision earlier did not bound every field. **Fixed**: containers are named, not dumped (`<object>`/`<array>`, with a differential pair showing the previous revision leaking a nested 400-character string); truncation shows head **and** tail with the true length, because the part of a long `upload_url` that distinguishes it was being cut; `jq -s` plus a shape guard means one response yields one summary and empty or multi-document input says `unreadable response`; status extraction runs under `LC_ALL=C` and is relabelled to "status-like numbers mentioned in stderr" since a response can write `mystatus=403` and forge one. **Verified**: jq's own stderr at the six stdout-only shape checks is not a leak — measured on jq 1.8.1, it names a value only for typed operations and truncates to ten characters, and every typed operation there is type-guarded first, so reachable errors name the index (`shape_jq_stderr`); the discard-site census is 9 + 6 + 1 = 16, which corrects both my "11" and the board's "10". **Accepted as limits, not bugs**: a value can spell a truncation marker (`+(999ch)`) and look clipped; caps slice by codepoint while the byte sanitizer runs after, so grapheme clusters can split; jq accepts `NaN`/`Infinity` and last-duplicate-key wins; `dotnet publish … 2>&1 | tee` stays unfiltered because compiler output is its purpose. **Still open after this round**: the canary (TASK-42, name burned), the repository controls (TASK-38/45), readback of **served** bytes plus a build attestation (TASK-48), and the `GET`-to-`PATCH` race no YAML change can close. Reviewer ran GNU grep 3.11, bash 5.2, jq 1.7 locally, no `gh`, no runner, no live calls. Harness 110 → 115 cases, 0 failures, exit 0. |
| Plan review: repository controls around release tags and releases (immutable releases + a `v*` ruleset, before the first tag) | **completed, dispositioned in v0.12.20; the plan is amended, not executed**. Three of my premises were wrong, each checked afterwards against the GitHub OpenAPI description held on this machine. (1) `enforcement` for a ruleset is `disabled | active | evaluate` — the value I had planned (`evaluating`) does not exist, and `evaluate` is **Enterprise-only**, so the "create it observe-only, flip it after the canary" ordering is not available: a ruleset is either enforcing or not. (2) **An immutable release can be deleted, and once it is, its tag can be deleted too — but the version name is burned for good**, so a disposable canary is a contradiction in terms once immutability is on: `v0.0.9` is a permanent reservation either way, and disabling the setting later does not unlock a burned name. (3) Whether `POST /releases` refuses a second release on one tag (a `422`) is **not documented here and was never tested**: the SPEC's opposite claim is corrected above. **Amended sequence**: check what the token and the repository actually allow (measured since: 0 tags, 0 releases, `main` unprotected, Actions `allowed_actions: all`, no SHA pinning rule, `contents: write` held by the preflight and publish jobs, no org visibility for this token); settle the duplicate-release and tampering questions in a throwaway repository, not in this one; then enable immutable releases and an **active** `v*` tag ruleset (`creation`/`update`/`deletion` are real rule types; `bypass_actors: []` means nobody — not an admin, not the Actions app — may move, delete, or in the creation case *cut* a `v*` tag, and repo admins can always edit the ruleset itself, so it is not admin-proof); and only then the owner-approved canary, as a **permanent prerelease that is kept**, checking tag sha, five asset names and digests, the per-release `immutable: true` flag and the release attestation. On failure: preserve the draft, **never auto-delete an uncertain release** — cleanup's `GET`-then-`DELETE` window and a burned tag name make a hasty delete worse than a stray draft. **What the two controls do not do**: they do not serialize a `GET` against a `PATCH`, they do not protect drafts before publication, they cannot stop a malicious *first* publication (immutability can seal bad bytes), they do not stop metadata edits (title, notes, prerelease, latest), and the `.github/workflows/` resolved-target guard is a separate Workflows-write permission that `GITHUB_TOKEN` can never hold. The review ran no repository calls. |
 **completed, dispositioned in v0.12.20**. **Verdict taken**: the constructed upload address is defense in depth; the stderr sanitizer and the per-field summary did not do what their comments claimed. **Fixed**: the preflight read now reports only the exit code and an extracted three-digit HTTP status — pattern redaction of stderr was shown unreliable (short tokens, Basic auth, `x-access-token` URLs, base64 with `+`/`/`, a credential split by the byte cap) and the channel is removed instead of filtered (`pre_errtoken`); the refusal summary is capped per field with true lengths (`name_crowding`), a wrong-typed field prints as JSON (`url_wrong_type`), and the previous version of that summary printed the whole response because `tojson` was applied to the input rather than to the field. **Verified, not assumed**: the address literal in the file is a plain string (the reviewer's Markdown-link suspicion was a rendering artifact of my own payload — `cat -A` on the line), and the `?name=` query can only ever carry ids from an allowlist of literal leg names. **Limits**: built address vs validated address differ only by owner/repo casing, deliberately, and a mid-run rename or a case-sensitive path would break loudly; the diagnostic sanitizes rather than redacts; the `EXIT` trap is best effort. **Reviewer ran jq 1.7 and bash 5.2.37 locally, no gh, no workflow, no live calls.** Harness 109 → 110 cases, 0 failures; no tag has run the gate. |
| May a release tag exist before the tag-protection ruleset protects it — and is that a hole? | **accepted gap, deliberately**: the ruleset carries `update` + `deletion` with **no** `creation` rule, so a new `v*` name can be cut at any moment and is frozen only once it exists. Measured on an unpublished `v*` tag: a forced move and a deletion are both refused (`GH013` — `Cannot update this protected ref.` / `Cannot delete this tag`), while creating a new `v*` name succeeds. A `creation` rule would close the window, but whether it also stops the account the gate pushes with is **untested**, and a wrong guess there means nothing can be released at all; the probe belongs in a disposable repository, and no SPEC sentence states its outcome before that probe returns. The mitigation is in the gate rather than in ref rules: the publish step re-resolves the tag and refuses unless it resolves to the commit the producing legs built, and checkout pins that same commit, so a tag moved between preflight and publish cannot publish the wrong source. |
| Plan review of the no-capacity refusal, before the code existed (TASK-35) | **completed, adopted with two changes.** Both blockers were reproduced against this code first. (a) The catalog is **not** closed-world authority: the scheduler's gate is `LoadedModels ∪ CatalogModels`, and `LoadedModels` comes from the engine's own `/models` endpoint, so a model loaded out-of-band is servable and a catalog-only refusal would have broken a working deployment — pinned by `SchedulerTests.Serves_A_Reported_Model_That_No_Catalog_Entry_Names`. (b) A refusal evaluated only at dispatch reasons about each model queue's **head**, so a hopeless ticket behind a stuck head would still hang: the check moved to admission, immediately after the `request.enqueued` event, so every admitted ticket is classified independently of head-of-line progress. Adopted as the review asked: a typed capability predicate (`EngineRuntime.CanServeModel`, precomputed into the scheduler's view) instead of a control-flow string inside `Wait(reason)`; unknown capability (an engine that has never answered the probe) is never hopeless; the zero-engine case needs no branch because config validation forbids it. Rejected as inapplicable: the "does overflow beat the capacity refusal" ordering worry — queues are per-model, so a hopeless model never fills one; the corner is stated in §7 and tested from the other side (five hopeless requests leave the cap free for serviceable traffic). Deferred to their own tasks: the serving-ladder fast path (its required-context figure is partly an estimate) and the absent ceiling for a legitimately queued request with no `max_queue_time`. |

| Plan review of the publication-honesty change, twice before the code existed (TASK-55) | **completed, verdict REVISE both times, and both rounds changed the design.** Round 1 — the reviewer's correction beat my plan and my own proposed fix was **withdrawn**: I had intended to write idle state before clearing the switch marker, and the review showed the scheduler takes "switching" from the snapshot marker rather than from the runtime's private switch state, so that order would have shown a mid-switch engine as idle and routed a request into it. Reproduced in code before accepting. Adopted from that round: the generation fence re-checked **inside every publish retry** instead of only at fetch start (a poll that learned nothing before a switch could otherwise land afterwards and replace what the switch proved, and the resulting false absence refuses a model that is being served); unknown-at-every-uncertain-exit rather than only on outright failure, with failure publications health-only so they cannot write a model list captured before the mutation; content-equality for quiet polls, with order and duplicate-count churn declared out of scope; and the finding that the exposed `string[]` in the snapshot makes "immutable" untrue — recorded as its own task rather than claimed fixed here. Round 2 — adopted a single await-free transition gate covering both the claim and the end of a switch (a switch's end runs outside the switch lock, so a second waiter reaches the claim-or-coalesce decision exactly there), the successor guard placed **inside** that gate (without it a late end erases a successor's marker — the review's claim, reproduced), the constraint that the gate is never taken before the switch lock because the lock's holder calls the end, and that the readiness task is settled outside the gate so a held gate cannot strand a waiter. Rejected after checking this code: that the existing switch events already wake dispatch in time — they are emitted before the transition commits, so the woken pass sees the marker up and skips the engine, which is why the commit signals dispatch directly and no periodic pump was added. **Accepted gap, stated in §5.5:** the fence orders publications rather than reality, so an engine restarted outside the router's sight evades it. **Attempted and dropped:** a test that starts a second waiter inside the end-of-switch window to pin the gate's exclusion directly; it reproduced the hazard, but under thread-pool pressure its own scheduling decided the outcome, so it was removed instead of shipped as a flake (TASK-59 AC #3) — the ordering is pinned structurally and by the marker-before-claim and stale-end tests.
| Deep review of v0.12.18 (release gate: what the run routes, what it logs, and what a shape check cannot prove) | **completed, dispositioned in v0.12.19**. **Fixed**: the asset upload target is **constructed** from repository context plus the validated digits-only id instead of being cut out of the server's `upload_url` (the response must still equal that exact address, so it is a contract check rather than a routing input) — `built_upload_target` counts 5 uploads at the constructed address and 0 at the answer's; the release-list read's captured stderr is limited to the first line of 2 KiB with credential-shaped runs replaced before the sanitizer, and its temp file is removed by an `EXIT` trap — `pre_errtoken` shows a 40-character token staying out of the log; the create-refusal diagnostic is per-field with the address bounded to 60 characters plus its length — `diag_fields_survive` keeps id/title/draft/prerelease visible. **Claim rejected after checking the code**: that case-folding the owner/repo prefix is a weakening introduced here — the replaced compare folded the same prefix, and the fold-scope rule dates to v0.12.15. **Now written as limits, not gaps**: exit status and shape prove the release-list read parsed, not that it was complete (a valid, shorter array is undetectable; a duplicate `tag_name` key defeats the match because jq keeps the last value, and nothing here reports duplicate keys); the id guard validates jq's printed form rather than the wire lexeme (`1e0` → `1` is accepted by design); `< 2^53` rejects a future larger id deliberately; a killed step can leave the temp file. **Review ran jq 1.7, not this machine's jq 1.8.1, and ran no gh, bash 5.3.9, workflow or live API call**, so its numeric-rendering claims were re-checked here before use, and its worry about Markdown-escaped YAML bytes was checked against the file: the anchors are literal and the cases prove them. **Not closed**: the read-to-`PATCH` race and cleanup's `GET`→`DELETE` window (rulesets TASK-38, restricted publishers, immutable releases TASK-45 — off). A wrong-typed `upload_url` also made the diagnostic collapse to "unreadable response"; it now prints the value as JSON (`url_wrong_type`). Harness 104 → 109 cases, 0 failures; no tag has run the gate. |
| Deep review of v0.12.17 (release gate: what a failed page leaves on stdout, what a projected field's equality proves) | **completed, dispositioned in v0.12.18**. **Fixed, each reproduced against 05f224b first**: the preflight release-list read still failed open — `\|\| true` discarded gh's exit status, and gh 2.46.0 prints the pages it fetched before exiting 1 on a mid-walk failure, so a half-read list is non-empty, the "could not read" guard never fires, and a release on an unfetched page reads as *no release*, which puts a second release on a tag that already has one (`pre_partial_page`); an error body was filtered away as no-match rather than refused (`pre_errdoc_body`), and the junk-element case had only ever been caught by a jq indexing error (`pre_junk_item` now names the rule). **A create response whose `upload_url` ends in a newline published**: the address was compared as a shell variable and command substitution strips the newline, so the compare saw an exact match — `pub_url_trailing_lf` runs 05f224b and it goes public; the address is now compared inside jq on the original JSON. A non-canonical numeric id (`1.0e2` → jq prints `1.0E+2`) is recorded as **not a bypass**: 05f224b stopped at the read-back, but after five uploads addressed from that spelling (`pub_id_exponent_old` counts 5, `pub_id_exponent` counts 0). The existing-release annotation could be split into a second workflow command by a string id carrying a newline; `jq -c` closes it (`pre_id_annotation`: 2 commands old, 1 now). **SPEC claim corrected in place**: `--paginate` does not answer one document per page — gh 2.46.0 merges into one array (`jq -s length` = 1 measured), and the shape rule holds under either. **Reviewer ran jq 1.7 / bash 5.2 / GNU tr 9.7, not this machine's 1.8.1 / 5.3.9**, so its printing claims were re-measured here before use. **Not closed, deliberately**: unsanitized `gh`/`jq` stderr on the remaining calls reaches the log as written; a release-write principal can still publish, replace assets, or duplicate the tag's release between the last read and the `PATCH` (and after cleanup's `GET`, before its `DELETE`) — rulesets (TASK-38), restricted publishers and immutable releases (TASK-45) are the controls, and no tag has run the gate. Harness 90 → 104 cases, 0 failures. |
| Deep review of v0.12.16 (release gate: how many answers a check accepts, what a regex anchors, what a sanitizer strips) | **completed, dispositioned in v0.12.17**. **Fixed, each reproduced first**: a check that pipes an answer through `jq -e` judges a multi-document answer by its **last** document, and the read-back, the un-draft `PATCH` and the cleanup lookup extract no fields, so v0.12.16 published on a read-back that answered someone else's published release first and deleted a **published** release on a lookup that answered `draft:false` then `draft:true` (`hole_readback_two_docs`, `hole_cleanup_two_docs`); `^…$` in jq's Oniguruma also matches before a trailing newline, so `state "uploaded\n"` and an asset name ending in `\n` cleared the hygiene patterns and the one-row-per-asset snapshot lost a field boundary (`\A…\z` now, and `pub_asset_*_newline` — **no demonstrated bypass in either revision**, so these are pattern cases, not a hole claimed); `type == "number"` admitted fractional, negative and exponent ids that this step interpolates into an upload address; one page of 100 assets is only an inventory while the page is not full, so a declared set of 100 or more is refused before create. **Also fixed, found while re-reading rather than by the review**: the preflight release-existence check swallowed API failures (`2>/dev/null || true`), so an unreadable list read as "no release for this tag" and the build went on to put a second release on a tag — the harness runs v0.12.16 failing open there. **Claims this SPEC had made that are now corrected in place**: "no server-chosen text reaches stdout" (false: diagnostics carry validated names, ids and sanitized field text — the accurate claim is that the success record is assembled from verified values); `target_commitish` omitted makes the workflow-write guard's comparison "empty by construction" (not guaranteed — the field is documented as unused when the tag exists, so the resolved target is GitHub's; both directions are canary evidence); and the asset `state` story (`uploaded` is documented and compared exactly, `uploader` is a different object field, a failed upload may leave `starter`, and digest availability beyond the docs example is a first-tag reading). **Deliberately not closed, and not closeable in YAML**: reads and the `PATCH` still do not serialize against another writer, so a release writer can retarget, retitle or replace assets between the last read and publication, and a tag writer can move the tag — tag rulesets (TASK-38), a restricted set of publishers, and immutable releases (TASK-45) stay the controls, and immutability does not stop a malicious *initial* publication; a response forged between this run and GitHub is outside any authority boundary the gate has, so the duplicate-key and counterfeit-field variants are stated as limits rather than chased; the ceiling refuses rather than paginates, which is the cheap correct shape while the declared set is five. **Harness as of this revision**: 90 cases, 0 failures, including the first differential pairs run against v0.12.16 itself (`bodies_at("fd76021")`); it still proves control flow and response reading only, and no tag has run the gate.
| Deep review of v0.12.15 (release gate: how a response is read — field boundaries, types, and what a joined string can be made to say) | **completed, dispositioned in v0.12.16**. **Fixed, each with a counterexample in the harness**: a response projected with `join(" ")` and re-split with `read` has fewer fields than it appears to — a null collapses to nothing and a space inside a string value becomes a separator — so a create answer of `name "v0.1.0 true"` with `draft` absent passed the tag, title, draft and prerelease comparisons at once; an asset named `"<wanted> uploaded <size> <digest>"` reported its own size and digest into the columns being compared, so four bytes of junk passed as the staged file while the asset's honest metadata sat unread further along the same line; `draft: "true"` (a string) passed as a draft and the run un-drafted on it; and a cleanup lookup answered with `tag_name "v0.1.0 true"` and `draft` null passed as this run's own draft, which would have deleted a **published** release. The comparisons are now `jq -e` over the JSON with typed equality, expected values passed as jq arguments rather than interpolated into a filter; the asset list's length, name characters and field types are required before any of it is parsed; a response's own text is escaped and truncated before it reaches the log, because the runner reads stdout for workflow commands. **Verified rather than assumed**: the exact `assets{?name,label}` template is what the create and get examples show and cutting at `{` is the right cut; `${var,,}` is bash 4+, ASCII-stable for these URLs, and an empty read fails the compare rather than passing it; a supplied `name` is used rather than a generated one even with `generate_release_notes=true`; `GET /releases/{id}` is documented to answer the id asked for, so checking the returned id stays; GHES answers a different upload host and path, so github.com-only exactness is a deliberate choice and not a portable claim. **Recorded where the review stops short**: it held that the two whole-string read-back comparisons were safe — true against the null-collapse it was testing, false against a documented boolean arriving as a string — so those comparisons are gone too. **Still not closed, and not closeable in YAML**: a release writer can retitle, re-tag, add or replace assets, or publish this draft between the last read and the `PATCH`, and a tag writer can move the tag afterwards; immutable releases (generally available, off by default, a repository/organization setting) lock assets and the associated tag only *after* publication, leave title, notes and prerelease/latest editable and drafts fully mutable, so cleanup must never widen to a published release — the operator decision on the board stands. **Folded, not deferred from the same round**: the case-fold covers only scheme/host/owner-repo (a differently-cased path segment from the release id on is refused); the un-draft `PATCH` response is read and checked as a release object, and its failure text does not imply nothing shipped; the run's record of what went public is printed from verified values, so no server-chosen text reaches stdout. **A trap the harness caught in the act**: `${var:0:$n,,}` is an arithmetic error in bash that does not abort a `set -euo pipefail` step, and it made six `upload_url` cases pass for the wrong reason until the expansion was split. **Counter-evidence against one suggested repair**: passing `target_commitish=<pinned sha>` on the create was proposed as a hardening and is not taken — the endpoint note makes the workflow-write guard a function of the *resolved* target relative to the default branch, and the Actions `GITHUB_TOKEN` can never satisfy that guard, so naming the tag commit turns a comparison that is empty by construction (omitted target = default-branch head) into one this run cannot evaluate before the call. What the tag commit resolves to, and what happens when it is behind the default branch, is a first-tag question, now written into §14. **Harness as of this revision**: 73 cases, 0 failures, over a stub `gh` that now builds the JSON each endpoint answers and applies the caller's own `--jq` to it the way gh does, so older revisions are shown reading the same answer through the projection they asked for. That still proves control flow and response *reading* only — nothing about GitHub's authorization decisions or the bytes on the network — and no tag has run the gate. |
| Deep review of v0.12.14 (release gate: the upload address, what a read-back identifies, what a digest proves) | **completed, dispositioned in v0.12.15**. **Fixed**: the `upload_url` compare is exact against the documented template rather than a prefix (the prefix accepted `assets-extra` and an unrequested query), case-folded so the API's canonical owner/repo spelling is not a false rejection, with uploads addressed from the returned URL; the release id and the pinned title are checked in both inspections and in the post-publish read-back. **Corrected in this SPEC**: what the stored-digest match proves (GitHub's reported digest equals the staged bytes at read time — not which call uploaded them, not what a download serves); what the scripted harness proves (the exercised mocked branches only — coverage and pass counts were not independently verified by the reviewer either); gh's token attachment is conditional, not unconditional. **Verified, not assumed**: gh v2.46.0 opens `--input` and stores the file as the request body with an explicit `Content-Length`, passes a reader straight to `http.NewRequest` (JSON marshalling happens only for map parameters), honours a supplied `Content-Type`, uses a `://` endpoint as an absolute URL, and looks the token up via hostname normalization; `${upload_url%%\{*}` is the right cut for `{?name,label}`. **Deliberately not closed**: a release writer can still retitle, re-tag, replace assets on, or publish this draft between the last read and the `PATCH`, and a tag writer can move the tag after its final resolution — more reads move the window, they do not close it; `.author.login` would add a weak creator check only if the expected principal were stable, and would not stop a later authorized writer. **New, and not a code change**: GitHub's immutable releases lock assets and the associated tag after publication and emit attestations, the strongest available control for the post-publication half of this problem; enabling it is a repository/organization setting, so it is an operator decision on the board. **Harness counts are as-of**: the earlier rows in this table quote the case counts of their own round; the harness runs 59 cases at this revision, and what it does not prove is unchanged. **One review finding was an artifact of my own prompt**: the round-7 “blocker” was Markdown link syntax wrapped around the expected URL in the request I sent — `cat -A` on the file shows a plain URL, so nothing shipped broken. |

| Deep review of v0.12.13 (release gate: what the create response proves, the upload address, the second look) | **completed, dispositioned in v0.12.14**. **Fixed**: the create response is validated as a whole (`tag_name`, `draft`, `prerelease`) before any upload; `upload_url` must name this repository and this release id; the release object's `tag_name` is checked in both inspections and in the post-publish read-back, not once before the first snapshot; the release title is pinned to the tag. **Corrected in this SPEC**: the workflow-write guard answers 404/403 rather than 422, and the same note applies to the update endpoint. **Verified, not assumed**: gh 2.46.0 passes `--input` as a raw reader with no JSON re-encoding and honours an explicit octet-stream content type, sends absolute endpoint URLs as given, and attaches the token by hostname normalization — so the upload leg is documented, and the digest match is what proves it behaved on a given run. **Deliberately not closed**: a response substitution or replay that carries a valid id and the right fields is not distinguishable by reading, so ownership rests on the create call plus tag protection and a restricted set of release writers (repository settings); reads and the un-draft `PATCH` are not atomic, and no number of reads makes them so; the generated notes body is GitHub's text, unreviewed by the gate; the workflow-write guard's behaviour for an existing tag is unknown until a run. **Harness limits, stated plainly**: 52 cases over a scripted `gh` prove this step's control flow and that it addresses the endpoints the way the step intends — they prove nothing about GitHub's authorization decisions, the real request bytes, or first-tag success. The one cheap live falsifier is a canary run on a disposable tag with the job's own token, which is an operator decision (it publishes for real for a few minutes), not a code change. |

| Deep review of v0.12.9–v0.12.12 (release gate: draft visibility, snapshot coherence, the publish race) | **completed, dispositioned in v0.12.13**. The load-bearing finding was against this SPEC's own reasoning: `GET /releases/tags/{tag}` is documented as "Get a published release with the specified tag", so the id that v0.12.11 said proved ownership could not be read for a draft at all. **Fixed**: create is `POST /releases` and the id and `upload_url` come from its response, uploads are addressed by that id, the draft is verified from one asset-list snapshot instead of five per-name lookups, and both the snapshot and the tag resolution are repeated immediately before the un-draft `PATCH`. The stub `gh` refuses the tag lookup, so v0.12.12's step is demonstrated failing rather than remembered as wrong. **Recorded rather than fixed**: the byte-size comparison is redundant once the stored digest is trusted and is kept for diagnostics; the byte fidelity of `gh api --input` (that the uploaded bytes are the staged bytes) is proven only by the stored digest matching, which is a first-tag confirmation and not a local one; a create whose response is lost leaves a draft this run cannot name — the `create_attempted` annotation and the preflight refusal are the whole story there, and re-running needs a human to remove the orphan; `if: failure()` does not run for a killed or cancelled runner, so a run that dies mid-publish has to be reconciled by hand against the releases page; a `GITHUB_TOKEN` write does not trigger downstream `on: release` workflows, so any notification keyed on publication is unverified until the first tag, as is whether creating a release for a tag whose commit touches `.github/workflows/` needs the job's token authorized to modify workflows (the endpoint's own note says it does in that case, and the least-privilege permission set is kept until a tag says otherwise). The published bytes are still never downloaded and re-hashed by the gate (the row above stands), and release-tag protection plus who may publish stays a repository-settings decision. |

| Deep review of v0.12.9–v0.12.11 (release gate: artifact forgeries, cleanup ownership, the gh CLI contract) | **completed, dispositioned in v0.12.10–v0.12.12**: four artifact forgeries (an artifact named for two legs, a regex dot standing in for `air.exe`, an extra archive member, a symlink or directory wearing an expected name) are each reproduced as a forgery that passed the shipped gate and then fixed, and are standing cases in the local gate harness; cleanup ownership moved off `github.run_started_at` (not a documented Actions context property, so read as empty it protects nothing) to the release id this run captured; and the publish path moved to REST because `gh release create` has no `--json`/`--jq` (gh 2.46.0 fails at argument parsing) and `gh release view`/`edit` resolve their operand as a tag name, not a release id. The harness's stub `gh` was what let the contract error survive two reviews: it now enforces the documented flag set. **Accepted gap, and this is the row §14's published-surface bullet points at**: the gate compares each remote asset's name, `uploaded` state, byte size and GitHub's reported stored-bytes `digest` against the staged file, but never downloads the served bytes and hashes them itself — it proves what GitHub says it stored, not what a downloader receives. Closing that fully means a post-publish download-and-hash leg or a signing/attestation story, which is runner minutes plus a trust-anchor decision rather than a constant; the published per-archive sidecars and `SHA256SUMS` remain what a downloader verifies. **Deliberately not closed**: workspace-internal link tricks above the leg folder, PAX extension handling and decompression bounds in the archive inspection, and the window between re-resolving the tag and creating the release — `concurrency: release-${{ github.ref }}` serializes this repo's own runs, but no workflow line excludes an external actor with write access, so release-tag protection and who may publish are repository-settings decisions for the operator. |

| Plan review of the publication-honesty fix set (TASK-61/62/63), before the code existed | **completed; the forced-CAS-retry pin is an accepted gap.** The claim's readiness answer and its switch marker come from one snapshot, and the marker goes up by a version compare-exchange on the version that answer read, so the raced second launch — a poll proving the model between the decision and the claim — is closed: the exchange loses, the caller re-decides on the newer snapshot, and the already-proved model is taken rather than launched again. **It is not pinned by a test.** Forcing the loser between its read and its exchange needs a seam in the middle of the claim's await-free gate region, which is more production surface than a race the code already excludes structurally is worth; the orderings that the same mechanism protects *are* pinned (marker-before-claim, a stale end cannot clear a successor's marker, a newcomer that arrives after the commit decides against an idle snapshot). Stated here rather than tested, so a reader knows which guarantee has a case and which has only the shape of the code. |
| v0.12.34's resume rule, red on `main`, and the half v0.12.35 deliberately does not close | **the rule was wrong, not the test; fixed forward in v0.12.35, not reverted.** v0.12.34 sent a caller whose own switch *succeeded* back through the claim path whenever `IsReadyFor` read false, and `IsReadyFor` reads false while `SwitchingToward` is set — so the marker of a **later** claim withdrew a proof this router had itself published. Measured: CI on that commit 191/192, the one failure `SwitchFairnessTests.AWaiterBehindAnotherProfilesSwitchTakesItsOwnTurn` at that caller's own ceiling; the commit's own new pin reproduced failing with launches `extra, m, extra, m`, two callers wanting different models on one engine alternating lifecycle transactions. v0.12.35 keeps the Failed-once rule and the caller-facing category wording, and narrows the re-decide to an observation that actually contradicts the target (§5.5). **Accepted admission/drain gap (TASK-64).** A waiter answered `Ready` from its own proof can resume after a later claim displaces the model. A fresh read at resume can itself go stale before the request reaches the engine. The admission/drain review of `fd5e0cd` called for exact-ticket commit ordered against admission closure. The injected scheduler witness (§5.5, `1061d70`) shows that when closure wins, an earlier pass leaves its exact ticket queued with zero upstream writes. The completion and profile witness (`e7586a3`) keeps the same preparation across both synthetic wakes while closed and sends the ticket's exact body once to the injected sink on matching Open. **Production admission remains open.** TASK-25 covers switch-ticket reservation, admission to the verified instance after `Ready`, and draining physical uses through late cleanup. Production activation requires the host fence (TASK-110) and instance-bound dispatch (TASK-118). TASK-72 owns the age-weighted policy for whose switch runs. TASK-61 carries TASK-25 and TASK-72 as dependencies. |
| Commit review of v0.12.42 (`14a01e2`: the refusal set, the door, the feed ordering) | **completed, verdict REVISE, dispositioned in v0.12.43.** A static review: it reran nothing, so the build, suite and mutation numbers on the v0.12.42 row stay this seat's. **Fixed in v0.12.43.** §5.5 said the shutdown answer reaches the caller through the outer catch as `engine_unreachable`; measured, the resume path throws cancellation and the request path's `OperationCanceledException` arm answers `cancelled` / 499 naming no model — the prose named the wrong arm, and a caller acting on it would have coded against a status the router does not send. `SwitchReason.All` was a `readonly` **array**, so its elements were rewritable by anything in the assembly, which is precisely what "closed set" forbids: it is a `ReadOnlySpan` now. The refusal walk recovered the caller's token by cutting the message at the last `": "`, which would have passed a detail of `anything: refresh_omits`; it asserts `Detail` whole. The door's rejection message quoted the offending value, which is text that can reach a caller through the same outer catch, so it names nothing. And the switch path's second ask of the selector predicate was unreachable — one predicate on both sides, the refusal in front of the claim — so it is deleted rather than kept as a guard no configuration can exercise, with a pin on the case that could actually have drifted: an entry with a **blank** `select.exec`, which a null-only check lets through to the provider. **Routed, not fixed here.** Subscriber isolation and emitting outside the switch lock go to **TASK-103**; the confirmed specifics are that a throwing subscriber at the never-started exit skips the caller's answer and the *observer's* text reaches the caller as `engine_unreachable`, and a subscriber that blocks on anything needing the switch lock blocks inside the append with the lock held. Gauge freshness — an unreported window counts as fitting, and an old positive gauge survives a probe that omits the field — is **TASK-68**. A structured reason field, so a caller stops parsing a parenthesised token out of a sentence, is **TASK-104**. **Accepted and now stated in §5.5:** the eight names describe what a completed attempt **observed**, not what the engine will do next; a timed-out wait is not an attempt and carries no name; `switch_error` also covers a refresh that cannot be read at all during a `wait_health = true` wait, because the attempt itself threw. |
| Commit review of v0.12.43 (`2f7e3e6`: the set cannot be edited, the whole-answer pins) | **completed, verdict REVISE; one finding was a real regression, fixed in v0.12.44.** The deletion of the selector ask inside `RunSwitchAsync` rested on "no configuration can reach it", which is false: the pre-claim check reads `_def.Catalog` and the switch reads `_config.Engines[_def.Name]`. Two sources, so a same-name definition through the public constructor — or an entry swapped after the check — reached the unguarded lookup and answered `launch_failed` with the engine's proof discarded. Fixed by resolving **one** definition before the claim and passing it to the switch; `ADivergentDefinitionIsRefusedFromTheSnapshotTheSwitchWouldUse` pins both provider shapes and was run red on `main` first. **Wording narrowed:** §5.5 and the v0.12.43 row asserted the deleted branch was unreachable, and both now say what is actually true. `SwitchReason.All` is a span over a private static array — not editable through anything the type advertises, still writable by code inside this assembly — so the claim is "no caller-facing route to editing the set", not "the set cannot be written"; structural enforcement (a switch expression over the members) is a board call, not a code requirement of this commit. **Withstood review:** the whole-`Detail` pins and the cancelled/499 correction. |
| Plan review of the switch-refusal taxonomy (TASK-94), before the code existed | **completed, verdict REVISE, adopted in v0.12.42.** Three provisions the review added, each checked against this code first: the pin states its expected **literals** rather than reading them out of the production constants (a set read from the source proves only that the source agrees with itself, so a token swap between two exits stayed green); the set has **one owner** (`SwitchReason.All`) and the audit accounts for every failed `return` in the engine, so a tenth exit cannot pass silently; and the added `engine.switch_failed` record goes out **once, before waiters settle, with the end in a `finally`**. That last one is not theoretical — `EventRing.Append` calls every subscriber inside the lock, in the emitter's own thread, with no isolation, so an observer that throws reaches the code that emitted the event, next to a switch marker and a claim. The guard at this emit site ships here; the rule for every producer is **TASK-103**. **Two names decided here rather than copied from the review.** `remote_engine` is refused: the token names the relation (`no_lifecycle_owner`), not the config flag that answers it, so the reach/ownership split (**TASK-77**) will not cost a second wire break — and a remote engine stays a switch candidate, because its probes still publish proofs and `IsReadyFor` still answers from them. The request-side wait ceiling does **not** join the set: the set says why an attempt *stopped*, an expired wait's whole message is that nothing stopped, so §5.5 scopes the set to completed attempts and the ceiling keeps its milliseconds (AC #5's second arm). **Accepted gaps, each a name deferred rather than a name invented:** `launch_failed` still absorbs a failed stop or a failed ownership check inside one transaction — a `stop_failed` name is deferred, because today the caller's action (retry later) is the same for all three and a fourth token is only worth its wire cost alongside the reach/ownership split (**TASK-77**); the admin outcome gap was subsequently resolved by the asynchronous operation lookup in §6, which exposes a structured reason for the accepted POST; and the rejection the door throws for an unlisted category reaches the caller through the request path's outer catch as `engine_unreachable`, which the door and the audit exist to make unreachable rather than to specify. |

Findings feed §7 (TOML binding choice), §9 (AOT strategy), §5.4 (scheduler
heuristics). Spec revisions cite them.

### 15.1 Open questions (unresolved)

1. **Admin wire casing** — `/admin/*` JSON is PascalCase today (§6). Pin one
   convention (camelCase vs snake_case) before the Pi extension (§13) and the
   Blazor client (§12.5) start depending on it; a change is a breaking wire edit,
   so decide once.
2. **De-escalation / release policy** — when may the router shrink a context
   window back to a cheaper tier (§7.6 non-goal today)? Needs hysteresis: a
   switch is expensive, and the window also invalidates prefix caches.
3. **Cohort + residency policy** — how long a model/profile stays loaded for a
   thread cohort that may return, and how that interacts with priority classes
   (§12.3 hook; the Pi design pass flagged it as the missing piece for Pi's wait protocol).
4. **Daemon authorization posture** — when the container socket is unreachable or permission
   is denied, does the router refuse the switch and report, or attempt to elevate? Socket
   group membership is root-equivalent, so the default here is refuse-and-report; it needs an
   explicit decision because it changes what an operator sees during an outage (§5.3.1).
5. **Where launch tuning lives** — GPU selection, utilization, suffix-cache flags and
   qualification receipts are today supplied by a host-side launcher shell script. If the router
   drives containers directly, either the router declares them in TOML (one source of truth,
   more config surface, and the router must understand engine flags) or the launcher survives as
   a thin image-provided entrypoint (less router knowledge, tuning invisible to the scheduler).
   Recommended: hybrid — router owns identity, ports, mounts, GPU device requests and readiness;
   the image entrypoint owns engine-flag detail, surfaced back through the health probe.

## 16. Repository layout

```
SPEC.md, README.md, AGENTS.md (build/test conventions), LICENSE (MIT)
.local/ — gitignored: working notes, handoffs, anything machine- or
service-specific. Never published.
dotnet-tool-manifest? no — Cli ships as normal tool
src/Arboreal.InferenceRouter/{AirJson.cs, ...}
src/Arboreal.InferenceRouter.Server/
src/Arboreal.InferenceRouter.Cli/
tests/Arboreal.InferenceRouter.Tests/
bench/Arboreal.InferenceRouter.Benchmarks/   (JIT overhead harness)
docs/benchmarks.md                           (measured numbers + caveats)
samples/router.toml
```
