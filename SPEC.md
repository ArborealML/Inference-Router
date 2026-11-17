# Arboreal Inference Router — Specification

Status: draft, continuously revised (v0.12.20)
Target: .NET 11 (10 acceptable; 11 SDK 11.0.100 installed), Native AOT everywhere.
Repository: `ArborealML/Inference-Router` on GitHub (public); local checkout
`~/src/arboreal/inference-router`. Working notes live in `.local/`, which is gitignored.

## 0. Revision log

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
3. Can **switch the underlying engine and model** — on this machine that means
   driving `select-model` (backends: strata, hyperqwen, glm, deepseek-serving)
   and health-waiting on the resulting server; the mechanism is fully
   configured, not hardcoded.
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

- **Engine**: a configured inference backend: an OpenAI-compatible HTTP
  endpoint plus lifecycle commands (start/stop/select-model), health probe,
  and a model catalog. Engines are *declaratively configured*; the library
  ships only adapters for the *kinds* of engine, not specific backends.
  v1 kinds: `openai-compatible` (endpoint + optional lifecycle exec).
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
| /admin/* (control), /admin/events (SSE), /admin/stats
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
- `src/Arboreal.InferenceRouter.Cli` — `inference-router` tool: `serve`,
  `chat`, `complete`, `models`, `select-model`, `status`, `enqueue`; also
  library entry points for embedding.
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

```csharp
public sealed class EngineDefinition {         // from TOML §7
    string Name; EngineKind Kind;              // v1: OpenAiCompatible
    Uri Endpoint;                              // e.g. http://127.0.0.1:18021/v1
    string? ApiKey; string? ApiKeyFile;        // inline or file path
    LifecycleDefinition Lifecycle;             // optional start/stop/select
    ModelCatalogDefinition Catalog;            // models this engine can serve
    HealthProbeDefinition Health;              // probe + readiness predicate
    EngineCapabilities Capabilities;           // vision, suffixCache, slots...
}
```

The **engine registry** owns: enumeration, current loaded model set (from
health probe `/v1/models`-equivalent + `/props`-equivalent when configured),
and lifecycle execution. Lifecycle commands run via `Process` with
argument-template expansion (`{{model}}`, `{{profile}}`, `{{gpu}}`,
`{{suffixCache}}`), non-zero exit = failure, and are serialized per engine
(one switch at a time). **Model switching** = configured `select` command +
wait-for-ready loop against the health probe (configurable timeout, default
7200s to match select-model's strata wait).

Lifecycle exec contract (implementation notes, since these are the failure
modes that bite real `select-model` runs):

- Args are re-quoted with `CommandLineToArgvW` rules before being handed to
  `ProcessStartInfo.Arguments` (the parser .NET uses on Unix too), so a single
  configured arg may contain spaces — e.g. `args = ["-c", "select-model {{model}}"]`.
- stdout and stderr are drained **concurrently** with the exit wait: chatty
  commands (select-model prints progress) would otherwise block on a full pipe
  buffer while the router waits for exit.
- Timeout kills the process tree (`Kill(entireProcessTree: true)`) and emits
  `engine.switch_failed reason=timeout_after_<ms>ms`.
- Every failure path resets the engine's switch state, so one failed switch can
  never wedge the engine in "switching" (which would exclude it from scheduling
  forever).
- When the switch targets a serving profile, readiness requires the reported
  context window to fit it, not merely that the model id appears in the models
  list (§7.6).

#### 5.3.1 Container-native lifecycle (partly implemented)

Shell-outs to a launcher script are an integration cost, not a feature: they hide the
state the router needs, and a failed script leaves an orphaned container the scheduler
cannot see. The target design drives the container runtime directly.

**Implemented today.** The seam exists and is what the switch path runs through:
`IEngineLifecycleProvider` (in `Engines/Lifecycle/`) over an `EngineLifecycleCoordinator` that owns the
ordering, with the existing selector program wrapped as `DelegatedExecProvider` and the Docker Engine API
implemented as `DockerLifecycleProvider`. A provider answers four questions in a fixed order — what exists
that we own (`OwnedPeersAsync`), may this switch proceed (`PreflightAsync`, validate-only), stop these
units (`StopAsync`), start this exact target (`LaunchAsync`) — and reports `LifecycleResult` with a short
reason for the event feed plus one actionable sentence for the operator. The coordinator is single-flight
per engine, never hands an unowned peer to the stop step, and treats a **failed ownership query as a
refusal**, not as "nothing is running". Readiness is proved by whoever owns the engine's health probe (the
runtime), on the provider's `ReadinessPolicy`.

Config is per engine, and the two owners are mutually exclusive:

```toml
[engine.strata]
lifecycle = "docker"            # or "exec" (default): a selector program owns the switch
[engine.strata.docker]
image = "registry.local/strata:2026-11"   # required: the router never pulls
socket = "unix:///run/docker.sock"        # or http(s)://host:port
api_version = "v1.44"                     # pinned; a daemon below it is refused
router_id = "r1"                          # required; written into labels, the ownership proof
command = ["--model", "{{model}}", "--ctx", "{{profile}}"]
env = ["AIR_ENGINE={{endpoint}}"]
host_port = 18020
host_ip = "127.0.0.1"                     # the interface a published port binds to; loopback by default
container_port = 8080
remove_stopped = true                     # delete the containers we stopped, instead of accumulating them
stop_timeout_ms = 30000
wait_health = true
wait_timeout_ms = 600000
call_timeout_ms = 15000                   # one Engine API call; a wedged daemon must not hold a switch
```

`router_id` has no default on purpose: it is the label that says which router owns a container, and a
shared default on a host with two routers would entitle each one to stop the other's work.
`host_ip` exists because an empty `HostIp` in the Engine API means *every interface*, and an
unauthenticated inference backend on `0.0.0.0` is a public port; the router always names one, and
loopback is the default. `remove_stopped` is what keeps a long-lived host from collecting one stopped
container per model change.

`lifecycle = "docker"` with a model that still carries `select = { exec = … }` is a **config error**:
two owners cannot both prove ownership of one model's container. Ownership labels are
`arboreal.router`, `arboreal.engine`, `arboreal.model`; the daemon's label filter is an optimisation and
the labels are re-checked locally, so a daemon that hands back a foreign container does not make it ours
to stop. A daemon that is unreachable, refuses the router, or is older than the pinned API version
produces one actionable message and **no escalation** (§15.1 decision: refuse rather than re-exec under
sudo).

Not implemented yet, and not implied by the code above: image **identity** pinning (presence is checked,
`Id`/`RepoDigests`/platform are not compared), `mounts`/`gpu`/resource limits, exec frame demux, logs and
daemon event subscription, the `npipe` transport on Windows, closing admission and draining the cohort
before a mutation (see the reservation note below — this needs a lifecycle reservation, not an inference
slot), and the latched-failure rule that stops a dispatcher wake-up from turning a failed switch into a
retry. What follows is the target shape, kept here because the implemented part is a subset of it, not a
different design.

**Seam.** Extract `IEngineLifecycle` out of the switch path (keeping switch
serialization and failure reset exactly as above) and implement it over a fakeable,
cancellable `IContainerRuntime`:

```csharp
public interface IContainerRuntime            // Docker Engine API adapter; fake in tests
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

**Transport and AOT.** Raw Docker Engine HTTP: `SocketsHttpHandler.ConnectCallback` to a
`UnixDomainSocketEndPoint` (`npipe` on Windows via `System.IO.Pipes`), `HttpClient` based at
`http://docker/`, HTTP/1.1, no proxy/redirect, per-call timeouts, API version pinned
(`/_ping` + `/version` to negotiate, reject a daemon outside a configured min/max). All JSON
through a source-generated `JsonSerializerContext` — no Docker client library, which needs
reflection roots and breaks the zero-warning AOT rule (§9). Exec output is demultiplexed from
the 8-byte frame header (stream id + big-endian length) and a non-zero exec exit code is a
failure.

**Ownership gate.** Every container the router manages carries an ownership label from config
(e.g. `air.launcher=<engine>`); inspect/list happens **before** anything is stopped or started,
and a container without our label is refused rather than adopted. Image references must be
pinned (verify `Id`, `RepoDigests`, platform, layer digests); the router never pulls.

**Error fidelity.** `404` from inspect means *absent* and nothing else. `ENOENT`,
`ECONNREFUSED`, `EACCES`, TLS and timeout failures mean *daemon unavailable* — never
“the container is gone”, because acting on that confusion deletes other people's
containers. `409` is surfaced as a conflict, `304` treated as idempotent success, and an
uncertain outcome is re-inspected by immutable id.

**Transaction order (what a switch actually is).** A lifecycle change is one host transaction, not a
command: prove ownership and admissibility → prove asset/receipt/qualification/image-pin/privilege →
close admission for the engine group → let the admitted cohort drain → re-validate preflight resource
versions → stop **only owned** peers → launch the exact target → prove readiness (liveness, loaded
non-alias model, exact context, exact slots, required modalities, provider-exec verification) → publish
readiness and reopen admission. There is **no automatic retry, rollback or readiness fallback**; a
timeout leaves the target in place for inspection, and a failure on an exact target is latched so a
dispatcher wake-up cannot silently turn it into a retry. Same-target callers join one operation
(cancelling one detaches only that waiter); a competing target gets a transient busy result and its
request stays queued. Recovery treats any journal as advisory and re-derives truth from runtime state
plus probes — it never resumes an interrupted mutation and never adopts or relabels a resource it does
not own. All mutations bind **immutable container ids**, never names.

**Reservation semantics.** Today a dispatched ticket holds its engine-slot reservation across a model
switch (`RouterCore` reserves at dispatch and releases in the `finally` after the engine call). That is
defensible while an engine serves exactly one model — there is nothing else it could answer — but once
the router performs the drain itself the switch must be an explicit *lifecycle* reservation, not
inference capacity, or the scheduler will read a switching engine as a busy one.

Config additions (§7 sketch): `image`, `container_name`, `labels`, `entrypoint`/`cmd`
template, `env`, `mounts`, `ports`, `gpu` (device requests / `count = -1` /
`capabilities = [["gpu"]]`), `stop_grace_ms`, `restart = "no"`, resource limits, and
`daemon = "unix:///var/run/docker.sock" | DOCKER_HOST`. Podman's socket is supported as a
Docker-API-compatible endpoint at its own version floor; containerd-native is out of scope
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

- One global priority queue per (model-alias) — no head-of-line blocking
  across models; scheduler scans models in priority order.
- **Fairness**: within a class, `SubPriority` then FIFO; a `Weight` limits
  how many in-flight requests a thread may hold concurrently
  (`maxInflight = ceil(Weight)`), so heavy threads can't monopolize slots.
- **Overflow**: per-model queue cap → reject with `408`-mapped error code and
  event `queue.rejected`.
- **Cancellation**: the caller's `CancellationToken` is wired to the ticket at enqueue, so a client
  that hangs up (an HTTP disconnect is `RequestAborted`) takes the same path as
  `DELETE /admin/tickets/{id}`: a queued request gives its place back, an in-flight request cancels the
  upstream engine call and releases the engine slot. Resulting code: `cancelled`.
- **One terminal state**: every sink (complete, fail, cancel, queue rejection, queue deadline,
  shutdown) claims the ticket's terminal transition atomically; the first wins and later racers are
  ignored. A ticket therefore emits exactly one of `request.completed` / `request.failed` /
  `request.cancelled`, and capacity reserved for it is released once.
- **Queue deadline is a timer**: `max_queue_time` arms a timer when the request is enqueued. It bounds
  the *wait* only — after dispatch the deadline no longer applies. A dispatch-time check is not
  sufficient: a busy engine gives the dispatcher no reason to re-examine the queue.
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
  request it already accepted. The ceiling bounds what the caller is still owed, not merely the arrival of
  headers: for a stream it ends at the **first frame**, and for a non-streaming request (and for an
  upstream error body) it stays armed through the **body**, so headers-then-silence is a timeout rather
  than a hang (`HeadersWithoutABodyAreNotAnAnswerSoTheCeilingStillEndsTheRequest`). A generation in
  progress is never cut off: once the first frame is out the ceiling no longer applies, and a stream whose
  engine goes quiet fails **in band** — one `engine_timeout` error frame and `[DONE]` on a response whose
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
  streams SSE deltas verbatim as the engine produces them (no re-buffering).
- `POST /v1/completions` — same for legacy completions.
- `GET /v1/models` — union of loaded + catalog-visible models
  (`OpenAiModelList` DTO with `owned_by: "<engine>"`).
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
  reported window to fit.
- `GET /admin/status` — engines, loaded models, queue depths, in-flight,
  stats per thread/model/engine.
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
`RouterTicket.StreamReady` resolves with it (or with `null` at a terminal state); the relay
waits on that and emits a keepalive `data:` frame every 4 s while a request is still queued or
switching — `data: [router keepalive seq=7 status="switching model=qwen-long engine=hyperqwen"]`,
status-bearing only when the state changed. A stream that fails after it started ends with
`data: {"error":{…}}` then `data: [DONE]`, so a client that stops at the sentinel cannot hang and
can tell a truncated completion from a finished one. Poll-based handoffs stall time-to-first-token
by the whole poll interval — the benchmark harness measured exactly that before this fix (§14,
`docs/benchmarks.md`).

**Threading through HTTP**: clients convey thread + priority via headers:
`X-Inference-Thread: agent-42`, `X-Inference-Priority: high:200`
(`class[:sub]`), `X-Inference-Affinity: <normalized-prefix-hash>` (optional,
server-side prefix normalization is authoritative anyway (§7.6),
`X-Inference-Required-Context: <tokens>` (declares the context the request must
be served under; absent ⇒ the router estimates from prompt chars, §7.6). Every admitted request
carries its ticket id back on the response as `X-Inference-Ticket`, which is how a client reaches
`GET|DELETE /admin/tickets/{id}` (watch / cancel) without scraping the event stream. Body-only
callers (default thread `anon`, priority Normal) work unchanged.

Priority is **not** clamped to the configured default and the surface has no authentication, so a
caller can currently promote itself. Pinning that at the edge is v0.1 ingress work (§15.1); until
then the deployment posture is loopback-or-behind-your-proxy. `docs/ai-integration.md` states this
to integrators.

The shipped error contract is one lowercase body on every surface —
`{"error":{"type":"air_error","code":…,"message":…}}` — and the code picks the status:
`invalid_request` 400, `not_found` 404, `deadline_exceeded` 408, `cancelled` 499, `queue_overflow`
429, `request_body_too_large` 413 (over `max_request_body_bytes`), `engine_error` / `engine_unreachable` 502,
`switch_failed` / `model_unavailable` 503 (the first also covers a model switch that outlived
`switch_wait_timeout_ms`), `engine_timeout` 504 (an engine that accepted the request and then went quiet
past `response_timeout_ms`, **and only where no response has started**: a streaming request has already
committed its `200`, so the same failure reaches the caller inside the stream as one `engine_timeout`
error frame followed by `[DONE]` — a 504 therefore always means nothing was emitted yet, and a caller may
retry one),
`no_available_engine` 500 (`switch_in_progress` 409 and `unknown_profile` 400 on the admin side).
An upstream engine's own 4xx/5xx arrives as `engine_error` with the engine's message embedded, so a
caller can tell "my request was wrong" from "the engine is down". `docs/ai-integration.md` carries
the same table for integrators, and `ErrorContractTests` pins both the mapping and the casing.

## 7. Configuration (TOML)

Single file; loaded at boot; `--config` may repeat (merge, later wins).
No code changes needed to support new engines. Sketch:

```toml
listen = "http://127.0.0.1:8321"
default_model = "qwen-3.8-flash-next"
default_priority = { class = "normal", sub = 0 }
auto_switch = true          # router may trigger select-model when needed
switch_cooldown_ms = 15000  # don't thrash switches

[scheduler]
prefix_affinity_threshold_chars = 512
affinity_weight = 1.5
prefill_cost_alpha = 1.0
max_queue_per_model = 64
max_inflight_per_engine = 8
switch_wait_timeout_ms = 0        # ceiling on a queued request's wait for a model switch; 0 = none

[threads."agent-42"]
priority = { class = "interactive", sub = 10 }
weight = 2.0
model_affinity = "qwen-3.8-flash-next-coder"

# ── engines ─────────────────────────────────────────────────────────
[engine.strata]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18021/v1"
api_key_file = "/etc/arboreal/keys/strata-api-key"
health_probe = { path = "/props", expect = "total_slots>0", interval_ms = 2000, timeout_ms = 5000 }
models_endpoint = "/v1/models"     # loaded models + status
capabilities = { vision = true, suffix_cache = false, max_context = 65536 }
response_timeout_ms = 600000      # ceiling on one turn at this engine; 0 = no ceiling

[engine.strata.catalog."qwen-3.8-flash-next"]
aliases = ["flash-next", "default"]
profiles = ["max", "coder"]
select = { exec = "select-model", args = ["{{model}}", "--profile", "{{profile}}"],
           wait_health = true, wait_timeout_ms = 7200000 }

[engine.strata.catalog."qwen-3.8-flash-next-coder"]
aliases = ["flash-next-coder", "coder"]
profiles = ["max-coder"]
select = { exec = "select-model", args = ["{{model}}"], wait_health = true }

[engine.hyperqwen]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18020/v1"
api_key_file = "/etc/arboreal/keys/hyperqwen-api-key"
capabilities = { vision = false, suffix_cache = true, max_context = 131072 }
[engine.hyperqwen.catalog."qwen-3.8-27b"]
aliases = ["classic"]
select = { exec = "select-model", args = ["{{model}}"], wait_health = true }

[engine.remote-a]                     # future: remote peer (no lifecycle)
kind = "openai-compatible"
endpoint = "http://host-b.lan:8321/v1"
remote = true
```

Requirements: comments preserved for round-tripping is **not** required;
schema errors fail fast at boot with path-precise messages; unknown engine
kinds rejected. Config binding is source-generated (§9) — no reflection.

**Dotted model ids must be quoted.** TOML treats `.` as a table separator, so
`[engine.strata.catalog.qwen-3.8-flash-next]` silently nests under a `qwen-3`
table and yields a bogus catalog entry; write
`[engine.strata.catalog."qwen-3.8-flash-next"]`. `arboreal config --config <path>`
validates a file and prints the resolved engine/model/serving-ladder plan without
opening sockets or running lifecycle commands — the check to run after any edit
(a test asserts the shipped samples load as written).

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

GPU memory per slot scales with `n_ctx`, so a fixed engine that always serves the maximum
window costs concurrency. AIR therefore models each model as a ladder of **serving
profiles** — a context-window tier plus the slots it affords — and switches tier on demand
instead of permanently paying for the largest window:

```toml
[engine.strata.catalog."qwen-3.8-flash-next"]
aliases = ["flash-next", "default"]
default_profile = "128k-2"            # profile used for a plain model switch
serving = [
  { name = "max",    max_context = 98304,  max_slots = 4 },
  { name = "128k-2", max_context = 131072, max_slots = 2 },
  { name = "1m",     max_context = 1048576, max_slots = 1 },
]
select = { exec = "select-model", args = ["{{model}}", "--profile", "{{profile}}"], wait_health = true }

[engine.strata.health_probe]          # gauges read from the probe body
path = "/props"
context_path = "n_ctx"               # active context window (tokens); empty string → engine doesn't report
slots_path = "total_slots"           # active concurrent slots; empty → fall back to max_inflight_per_engine
```

Semantics:

- **Demand estimate**: a request's required context is `options.required_context` when the
  client declares it (the Pi extension can compute it exactly), else
  `ceil(prompt_chars / scheduler.chars_per_token_estimate)` (default 4.0).
- **Admission**: an engine's live capacity is the *tighter* of
  `scheduler.max_inflight_per_engine` and the slots it reports (`total_slots`);
  until the gauge is known the configured ceiling alone applies. A profile
  switch therefore lowers admission as soon as the engine reports fewer slots.
- **Escalation**: when `required > engine.active_context_window` (only when the engine
  reports a window — a blind engine is never escalated), the scheduler selects the
  *smallest* profile with `max_context >= required`, emits `TargetProfile`, and the ticket
  queues behind a lifecycle switch to that profile (`{{profile}}` expansion) instead of
  being compacted or rejected. `auto_switch = false`, no fitting profile, or a
  `wait_health` timeout ⇒ the ticket fails with `switch_failed` (never silently truncated).
- **Observability**: `engine.profile` events fire when the inferred active profile changes;
  `EngineStats` carries `active_profile` / `active_context_window` / `active_slots`.
- **Non-goal for now**: automatic *de*-escalation (shrinking a window when big-context
  demand goes away). Escalation is demand-driven; release policy is an open question (§15).

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

`RouterEvent` ring buffer (per-host, ordered, monotonic `Seq`, replayable; capacity 4096).
Replay is **exclusive** on its cursor: `since=n` means "after n". A cursor inside the retained window
gets every event still held for what was asked — there is no smaller cap than the ring itself — and a
cursor ahead of the ring (a client reconnecting with a sequence from a previous router lifetime) is
clamped to the current head, so the live tail still flows instead of a feed that can never deliver
anything. The retention boundary is the only place events disappear. A subscriber's snapshot and its
live subscription are taken together, so an event appended between the two is neither lost nor
delivered twice. The feed is also **causally ordered**: every terminal outcome — an engine switch, a
completed, cancelled or failed request — is appended before the caller waiting on it is woken, so a
client that awaits its own ticket and then replays its history, or an operator tailing `/admin/events`,
never arrives a moment before the explanation does. Subscribers get a bounded buffer (256 events) and the feed of a consumer that falls
behind is **ended** rather than silently pruned: on a filtered feed a dropped event leaves no `Seq`
gap, so an ended stream is the strongest signal available — reconnect from the last `Seq` you saw. It
does **not** say how much was missed, and it looks the same as a feed that ended normally; nothing
distinguishes a restart from a quiet period either, because the ring has no epoch. Reporting loss, and
naming the epoch, need new fields on the wire (`stream.gap`, below) and are still open.
Live subscriptions are bounded by `max_event_subscribers` (256 by default; the count includes the
router's own internal subscription, so the key must be at least 2 to let one client feed through), and
a feed that arrives when the ring is full is refused with `503 too_many_subscribers` **before** any SSE
headers go out. A subscription ends when the router shuts down, and a caller that hangs up stops being
delivered to. Field names on the wire are PascalCase (§6).

- `thread.registered`, `thread.priority_changed`
- `request.enqueued {thread, ticket, model, priority, promptChars}`
- `request.selected {ticket, engine, model, affinityScore, busyScore}`
- `request.streaming_first_token {ticket, engine, msFromEnqueue}`
- `request.completed {ticket, engine, tokens, msFromEnqueue, msFromFirstToken}`
- `request.failed/cancelled/deadline_exceeded {ticket, reason}`
- `engine.health {engine, healthy, loadedModels}`
- `engine.switch_started/completed/failed {engine, model, profile, ms}`
- `queue.rejected {thread, model, reason}`

`/admin/stats` aggregates: per-thread (enqueued/completed/p50-p99 TTFT),
per-engine (inflight, queue, tokens/s estimate), per-model queue depth.
This feed is the Blazor client's contract (§12.5).

**Feed gaps that must close before a live view is worth building** (from the robustness
comparison; tracked as the parity task): ticket records need generated fields — owner,
state, priority, queue, attempt, active profile, required vs active context, capacity,
timing, result, prompt/completion bytes, tokens, prefix hash, config pin, commit boundary.
The event set needs rejection, capacity change, queue, health/crash/restart, switch/profile,
prefix, config change and an explicit **`stream.gap`** (append + counters only, never
content). `/admin/status` must add active/queued/readiness/rates; `/admin/events` must
support filtering, a heartbeat and bounded sequence-gap handling; a bounded
`/admin/requests` (recent-request journal) is needed for the request timeline. Pinning the
admin schema and casing (§15.1) is a prerequisite, not a follow-up.

## 11. CLI

```
inference-router serve [--config f.toml] [--listen http://...]
inference-router chat  [--server url] [--thread id] [--priority high:200]
                       [--model name] [-p prompt | --stdin]
inference-router complete [--server url] [...]
inference-router enqueue --server url --thread id --model m --payload f.json
inference-router models [--server url]
inference-router select-model <name> [--server url | --engine e] [--profile p]
inference-router status [--server url] [--watch]
inference-router config [--config f.toml]
```

`config` is the dry run: parse + validate + print the resolved engine / model /
serving-ladder plan, with no sockets and no lifecycle commands (safe on a machine
whose engines are down). `status` reports each engine's active profile, context
window and slot count alongside health, loaded models and in-flight count.

Library-first: every CLI verb is a thin wrapper over `IInferenceRouter` or
`RouterHttpClient`; no logic lives in the CLI layer.

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
Reads `/admin/events` (SSE) + `/admin/stats`; static SSR + selective
Interactive Server per T011 guidance; never bypasses the public API. Live
view = request log stream + per-engine gauges + queue depths.

Screens (our shape, not an appliance's): global inference activity · queues and
admission · engines/profiles/switches · topology and trends · request timeline ·
health, config and security. From the comparable NVIDIA router (Personal-AI-Router),
**borrow** the workload rail, filters, topology view, endpoint copy and actionable
error presentation; **change** capacity/profile/readiness presentation to be
engine-first rather than model-first, and add queue/TTFT/throughput trends;
**skip** the installer, model deletion, pairing flow, autostart/update/wipe controls.
The feed prerequisites in §10 gate this whole section.

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
  Before publication the tag is re-resolved and must still name the commit the legs built, and the
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
  already carries the tag, and that refusal covers a release list it cannot read as well: "could not tell" is not
  "does not exist", because GitHub allows two releases on one tag. macOS RIDs stay declared in the Cli project so a local
  `-r osx-arm64` build works, but no macOS artifact is built, published, claimed or gated: the account has
  no macOS runner minutes, and a leg that queues forever is not a leg that passes. A platform is added
  back with runner minutes, a leg that actually runs, `EXPECTED_RIDS` updated, and README copy that
  follows it. No tag has been published yet, so this gate is configured rather than proven; the first tag
  run is the evidence. The published `SHA256SUMS` and per-archive sidecars are a transfer and collection
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
| 1 URI + proven readiness + dispatch progress | **closed** | `EngineRuntime.Combine` one join rule (§7.5, `UriCompositionTests`); a switch is proven by health + loaded-model + window-fit probes and fails loudly with one actionable message (§5.3.1); dispatch and the feed relay are signal-driven (`_dispatchSignal`, `WaitToReadAsync`) — no polling loop, and the 200 ms TTFT poll is gone (v0.8.4) |
| 2 transport discipline | **closed on the request side** | bounded bodies ✔ (`max_request_body_bytes`, 413 `request_body_too_large`, §6); cancellation reaches the engine ✔ (§5.5 wire tests); lifecycle phases are timed ✔ (`stop/wait/call_timeout_ms`, health `timeout_ms`); and every request-side wait now has its own ceiling ✔ (`switch_wait_timeout_ms` for a switch the request triggered — over the ceiling the request fails `switch_failed` with its slot handed back; per-engine `response_timeout_ms` for a turn at the engine — over the ceiling it fails `engine_timeout` 504 and the slot is released, or ends in band when the response had already started). `RequestPhaseTimeoutTests` pins all three outcomes, including that a caller's abort still reports `cancelled` and not a timeout, and that a switch survives the waiter that started it. **Still open:** a wedged *engine process* is noticed through health probes rather than through the request path (§5.3.2), a switch reserves an inference slot rather than its own lifecycle reservation (TASK-25), and once a stream's first frame is out there is no bound on a reader that stops consuming or on an engine that goes quiet again mid-generation — the relay buffers the former without limit and waits forever for the latter (TASK-30) |
| 3 switch serialization + error contract | **closed** | single-flight per engine with `_switchState` reset on every failure path (§5.3.1); error codes and casing pinned by `ErrorContractTests` against the §6.1 table |
| 4 owned lifecycle | **partly open** | provider seam + coordinator + Docker provider shipped (§5.3.1, §15); request-side phase bounds now exist (§5.5), so a select command that hangs no longer parks traffic indefinitely — the waiting request fails at `switch_wait_timeout_ms` while the switch runs on; **open:** a switch still occupies an inference slot instead of its own lifecycle reservation (TASK-25), and the §5.3.1 non-claims — image identity pinning, mounts/GPU, in-place model mutation, latched failures (TASK-26) |
| 5 security | **open by deployment posture** | no ingress auth/TLS/rate caps yet: admin is unauthenticated and binds loopback (§6.1), engine keys are per-engine config; this is the v0.1 ingress work, not a silent hole |
| 6 P1 set | **open in order** | bounded telemetry with gap detection is half-done: ring bounds, exclusive replay, ended-on-slow feeds, causal ordering (§10) exist, but loss reporting (`stream.gap`) and the epoch are wire additions awaiting the live-view decision (TASK-21 → TASK-11); request-journal durability and the fairness/starvation bound are not started |
| 7 multi-host | **not started, deliberately** | §12.1–§12.3 stay speculative until rows 1–5 hold; no task is cut for it yet |

Nothing in the open rows is a surprise to the design: each is either a task on the board or a row here
with the reason it waits. What the review *did* change is the order of work and the refusal to call the
router robust until rows 2, 4 and 6 close.

## 15. Research questions and dispositions

| Question | Disposition |
|---|---|
| Cysharp-style performance guidance for this codebase | **completed** — no prior packet existed; the result is the style digest in §9.1. |
| AOT-safe TOML: CsToml vs Tomlyn on .NET 11 | **completed, confirms shipped choice**: Tomlyn 2.11.1 ships a Roslyn source generator (`TomlSerializerContext`, `[TomlSerializable]`, `[TomlPropertyName]`) — what §9/`ConfigDto` use. CsToml + CsToml.Generator 1.8.6 (prozolic, *not* Cysharp) is the alternative with per-property annotations. |
| ASP.NET minimal APIs under Native AOT on .NET 11; STJ source-gen for streaming | completed; dispositioned in §9 |
| Prefix/suffix-aware scheduling prior art (SGLang router, Llumnix, vLLM) | **completed**; distilled into §12.2/§12.3/§12.4 |
| Pi mid-session model switching + wait protocol design | **completed**; folded into §13.2/§13.3 |
| Recommended Roslyn analyzer stack for code quality on .NET 11 AOT | **completed, integrated** (§9.2: central packages, banned-API policy, warnings-as-errors, 0-warning build + 0-warning AOT publish) |
| Tomlyn DOM vs source-generated binding API on .NET 11 | **completed**: keep Tomlyn 2.11.1 source-gen; untyped `TomlSerializer.Deserialize<TomlTable>` is reflection-free/AOT-safe if a DOM path is ever needed (not needed today) |
| AOT-safe Docker access from .NET 11 (client library vs raw engine API) | **completed**: no client library — Docker.DotNet needs reflection roots (`DynamicDependency`, IL2026/IL3050 risk). Go raw: `SocketsHttpHandler.ConnectCallback` over `UnixDomainSocketEndPoint` (npipe on Windows), `HttpClient` based at `http://docker/`, STJ **source-generated** DTOs, API version pinned with `/_ping` + `/version` negotiation, exec demux of the 8-byte frame header, and error fidelity where **inspect 404 means absent and nothing else** while transport failure means *daemon unavailable*. Seam: `IEngineLifecycle` over a fakeable `IContainerRuntime` (§5.3) |
| Robustness and UI comparison against NVIDIA Personal-AI-Router | **completed**: verdict + ordering in §14.1, feed prerequisites in §10, UI borrow/change/skip in §12.5. Where that router leads on robustness we have tasks; where we lead (priority, admission, profiles, prefix, generic endpoints, engine keys, per-engine instances) is not a gap to close |
| Native engine-lifecycle design (transaction shape + container seam) | **completed**, adopted in §5.3.1: provider seam (`IEngineLifecycleProvider` over a coordinator), transaction order, single-flight per target with latched failures, ownership gate with no adoption of unowned resources, advisory journal, preflight as a validate-only verb, `select.exec` kept as a mutually-exclusive delegated provider. It preferred the docker CLI as the first Docker provider (smaller safety-critical surface); we keep **raw Engine HTTP** instead, because "no client library" from the review above is about reflection-rooted SDKs rather than `HttpClient` — the CLI stays as an operator fallback |

| Deep review of v0.12.4 (request-side phase timeouts) | **completed, dispositioned in v0.12.5**: the timer-stops-at-headers gap, the late-response leak, the switch running on the initiating request's token, and the switch-state wedge on `no_select_command` are fixed with tests; the stream channel is now closed by the ticket's terminal transition (which also fixes the silent stream end). Two findings are **accepted gaps**, not fixes: (a) the selector's own failure reason is recorded on the feed as `engine.switch_failed` and deliberately kept out of the caller-facing `switch_failed` message, so the caller sees the outcome and the operator sees the cause — change this when §13 needs to distinguish them programmatically; (b) a phase-timeout value above `Int32.MaxValue` ms is rejected at DTO binding (a Tomlyn/STJ range error) rather than by the router's own `RouterConfigException` message — either way startup fails with a non-zero exit, so the operator is stopped, only with a less specific line |

| Deep review of v0.12.5 (response gate, engine-owned switch token, stream-channel ownership) | **completed, dispositioned in v0.12.6**: fixed a false `Ready` — a waiter for one serving profile was answered by another profile's switch on the same model, so a request could be dispatched against a window it cannot serve (`SwitchFairnessTests` pins both directions: not ready on a stranger's success, not failed on a stranger's failure); fixed a shutdown race where disposing the switch token's source could throw before the switch task's own cleanup and strand every waiter on that engine (the token is read before the task is scheduled, the source is deliberately *not* disposed, and a switch that fails to start settles its waiters); the ceiling can no longer reassign blame for a failure inside the router's own terminal work, because the gate is passed the moment the owed bytes are in hand; stream acquisition sits inside the ceiling; a switch-failure message names the target that failed rather than whichever switch happened to be in flight. **Accepted deliberately**: a ceiling that loses the race with the engine's bytes does not fire (bytes in hand beat a timeout — the alternative is destroying an answer the caller can use), and there is **no idle bound after the first frame** — a stalled generation and a stalled reader are TASK-30, because each needs a wire decision (a truncation surface, a slow-consumer policy) rather than a constant |

| Plan review: repository controls around release tags and releases (immutable releases + a `v*` ruleset, before the first tag) | **completed, dispositioned in v0.12.20; the plan is amended, not executed**. Three of my premises were wrong, each checked afterwards against the GitHub OpenAPI description held on this machine. (1) `enforcement` for a ruleset is `disabled | active | evaluate` — the value I had planned (`evaluating`) does not exist, and `evaluate` is **Enterprise-only**, so the "create it observe-only, flip it after the canary" ordering is not available: a ruleset is either enforcing or not. (2) **An immutable release can be deleted, and once it is, its tag can be deleted too — but the version name is burned for good**, so a disposable canary is a contradiction in terms once immutability is on: `v0.0.9` is a permanent reservation either way, and disabling the setting later does not unlock a burned name. (3) Whether `POST /releases` refuses a second release on one tag (a `422`) is **not documented here and was never tested**: the SPEC's opposite claim is corrected above. **Amended sequence**: check what the token and the repository actually allow (measured since: 0 tags, 0 releases, `main` unprotected, Actions `allowed_actions: all`, no SHA pinning rule, `contents: write` held by the preflight and publish jobs, no org visibility for this token); settle the duplicate-release and tampering questions in a throwaway repository, not in this one; then enable immutable releases and an **active** `v*` tag ruleset (`creation`/`update`/`deletion` are real rule types; `bypass_actors: []` means nobody — not an admin, not the Actions app — may move, delete, or in the creation case *cut* a `v*` tag, and repo admins can always edit the ruleset itself, so it is not admin-proof); and only then the owner-approved canary, as a **permanent prerelease that is kept**, checking tag sha, five asset names and digests, the per-release `immutable: true` flag and the release attestation. On failure: preserve the draft, **never auto-delete an uncertain release** — cleanup's `GET`-then-`DELETE` window and a burned tag name make a hasty delete worse than a stray draft. **What the two controls do not do**: they do not serialize a `GET` against a `PATCH`, they do not protect drafts before publication, they cannot stop a malicious *first* publication (immutability can seal bad bytes), they do not stop metadata edits (title, notes, prerelease, latest), and the `.github/workflows/` resolved-target guard is a separate Workflows-write permission that `GITHUB_TOKEN` can never hold. The review ran no repository calls. |
 **completed, dispositioned in v0.12.20**. **Verdict taken**: the constructed upload address is defense in depth; the stderr sanitizer and the per-field summary did not do what their comments claimed. **Fixed**: the preflight read now reports only the exit code and an extracted three-digit HTTP status — pattern redaction of stderr was shown unreliable (short tokens, Basic auth, `x-access-token` URLs, base64 with `+`/`/`, a credential split by the byte cap) and the channel is removed instead of filtered (`pre_errtoken`); the refusal summary is capped per field with true lengths (`name_crowding`), a wrong-typed field prints as JSON (`url_wrong_type`), and the previous version of that summary printed the whole response because `tojson` was applied to the input rather than to the field. **Verified, not assumed**: the address literal in the file is a plain string (the reviewer's Markdown-link suspicion was a rendering artifact of my own payload — `cat -A` on the line), and the `?name=` query can only ever carry ids from an allowlist of literal leg names. **Limits**: built address vs validated address differ only by owner/repo casing, deliberately, and a mid-run rename or a case-sensitive path would break loudly; the diagnostic sanitizes rather than redacts; the `EXIT` trap is best effort. **Reviewer ran jq 1.7 and bash 5.2.37 locally, no gh, no workflow, no live calls.** Harness 109 → 110 cases, 0 failures; no tag has run the gate. |
| Deep review of v0.12.18 (release gate: what the run routes, what it logs, and what a shape check cannot prove) | **completed, dispositioned in v0.12.19**. **Fixed**: the asset upload target is **constructed** from repository context plus the validated digits-only id instead of being cut out of the server's `upload_url` (the response must still equal that exact address, so it is a contract check rather than a routing input) — `built_upload_target` counts 5 uploads at the constructed address and 0 at the answer's; the release-list read's captured stderr is limited to the first line of 2 KiB with credential-shaped runs replaced before the sanitizer, and its temp file is removed by an `EXIT` trap — `pre_errtoken` shows a 40-character token staying out of the log; the create-refusal diagnostic is per-field with the address bounded to 60 characters plus its length — `diag_fields_survive` keeps id/title/draft/prerelease visible. **Claim rejected after checking the code**: that case-folding the owner/repo prefix is a weakening introduced here — the replaced compare folded the same prefix, and the fold-scope rule dates to v0.12.15. **Now written as limits, not gaps**: exit status and shape prove the release-list read parsed, not that it was complete (a valid, shorter array is undetectable; a duplicate `tag_name` key defeats the match because jq keeps the last value, and nothing here reports duplicate keys); the id guard validates jq's printed form rather than the wire lexeme (`1e0` → `1` is accepted by design); `< 2^53` rejects a future larger id deliberately; a killed step can leave the temp file. **Review ran jq 1.7, not this machine's jq 1.8.1, and ran no gh, bash 5.3.9, workflow or live API call**, so its numeric-rendering claims were re-checked here before use, and its worry about Markdown-escaped YAML bytes was checked against the file: the anchors are literal and the cases prove them. **Not closed**: the read-to-`PATCH` race and cleanup's `GET`→`DELETE` window (rulesets TASK-38, restricted publishers, immutable releases TASK-45 — off). A wrong-typed `upload_url` also made the diagnostic collapse to "unreadable response"; it now prints the value as JSON (`url_wrong_type`). Harness 104 → 109 cases, 0 failures; no tag has run the gate. |
| Deep review of v0.12.17 (release gate: what a failed page leaves on stdout, what a projected field's equality proves) | **completed, dispositioned in v0.12.18**. **Fixed, each reproduced against 05f224b first**: the preflight release-list read still failed open — `\|\| true` discarded gh's exit status, and gh 2.46.0 prints the pages it fetched before exiting 1 on a mid-walk failure, so a half-read list is non-empty, the "could not read" guard never fires, and a release on an unfetched page reads as *no release*, which puts a second release on a tag that already has one (`pre_partial_page`); an error body was filtered away as no-match rather than refused (`pre_errdoc_body`), and the junk-element case had only ever been caught by a jq indexing error (`pre_junk_item` now names the rule). **A create response whose `upload_url` ends in a newline published**: the address was compared as a shell variable and command substitution strips the newline, so the compare saw an exact match — `pub_url_trailing_lf` runs 05f224b and it goes public; the address is now compared inside jq on the original JSON. A non-canonical numeric id (`1.0e2` → jq prints `1.0E+2`) is recorded as **not a bypass**: 05f224b stopped at the read-back, but after five uploads addressed from that spelling (`pub_id_exponent_old` counts 5, `pub_id_exponent` counts 0). The existing-release annotation could be split into a second workflow command by a string id carrying a newline; `jq -c` closes it (`pre_id_annotation`: 2 commands old, 1 now). **SPEC claim corrected in place**: `--paginate` does not answer one document per page — gh 2.46.0 merges into one array (`jq -s length` = 1 measured), and the shape rule holds under either. **Reviewer ran jq 1.7 / bash 5.2 / GNU tr 9.7, not this machine's 1.8.1 / 5.3.9**, so its printing claims were re-measured here before use. **Not closed, deliberately**: unsanitized `gh`/`jq` stderr on the remaining calls reaches the log as written; a release-write principal can still publish, replace assets, or duplicate the tag's release between the last read and the `PATCH` (and after cleanup's `GET`, before its `DELETE`) — rulesets (TASK-38), restricted publishers and immutable releases (TASK-45) are the controls, and no tag has run the gate. Harness 90 → 104 cases, 0 failures. |
| Deep review of v0.12.16 (release gate: how many answers a check accepts, what a regex anchors, what a sanitizer strips) | **completed, dispositioned in v0.12.17**. **Fixed, each reproduced first**: a check that pipes an answer through `jq -e` judges a multi-document answer by its **last** document, and the read-back, the un-draft `PATCH` and the cleanup lookup extract no fields, so v0.12.16 published on a read-back that answered someone else's published release first and deleted a **published** release on a lookup that answered `draft:false` then `draft:true` (`hole_readback_two_docs`, `hole_cleanup_two_docs`); `^…$` in jq's Oniguruma also matches before a trailing newline, so `state "uploaded\n"` and an asset name ending in `\n` cleared the hygiene patterns and the one-row-per-asset snapshot lost a field boundary (`\A…\z` now, and `pub_asset_*_newline` — **no demonstrated bypass in either revision**, so these are pattern cases, not a hole claimed); `type == "number"` admitted fractional, negative and exponent ids that this step interpolates into an upload address; one page of 100 assets is only an inventory while the page is not full, so a declared set of 100 or more is refused before create. **Also fixed, found while re-reading rather than by the review**: the preflight release-existence check swallowed API failures (`2>/dev/null || true`), so an unreadable list read as "no release for this tag" and the build went on to put a second release on a tag — the harness runs v0.12.16 failing open there. **Claims this SPEC had made that are now corrected in place**: "no server-chosen text reaches stdout" (false: diagnostics carry validated names, ids and sanitized field text — the accurate claim is that the success record is assembled from verified values); `target_commitish` omitted makes the workflow-write guard's comparison "empty by construction" (not guaranteed — the field is documented as unused when the tag exists, so the resolved target is GitHub's; both directions are canary evidence); and the asset `state` story (`uploaded` is documented and compared exactly, `uploader` is a different object field, a failed upload may leave `starter`, and digest availability beyond the docs example is a first-tag reading). **Deliberately not closed, and not closeable in YAML**: reads and the `PATCH` still do not serialize against another writer, so a release writer can retarget, retitle or replace assets between the last read and publication, and a tag writer can move the tag — tag rulesets (TASK-38), a restricted set of publishers, and immutable releases (TASK-45) stay the controls, and immutability does not stop a malicious *initial* publication; a response forged between this run and GitHub is outside any authority boundary the gate has, so the duplicate-key and counterfeit-field variants are stated as limits rather than chased; the ceiling refuses rather than paginates, which is the cheap correct shape while the declared set is five. **Harness as of this revision**: 90 cases, 0 failures, including the first differential pairs run against v0.12.16 itself (`bodies_at("fd76021")`); it still proves control flow and response reading only, and no tag has run the gate.
| Deep review of v0.12.15 (release gate: how a response is read — field boundaries, types, and what a joined string can be made to say) | **completed, dispositioned in v0.12.16**. **Fixed, each with a counterexample in the harness**: a response projected with `join(" ")` and re-split with `read` has fewer fields than it appears to — a null collapses to nothing and a space inside a string value becomes a separator — so a create answer of `name "v0.1.0 true"` with `draft` absent passed the tag, title, draft and prerelease comparisons at once; an asset named `"<wanted> uploaded <size> <digest>"` reported its own size and digest into the columns being compared, so four bytes of junk passed as the staged file while the asset's honest metadata sat unread further along the same line; `draft: "true"` (a string) passed as a draft and the run un-drafted on it; and a cleanup lookup answered with `tag_name "v0.1.0 true"` and `draft` null passed as this run's own draft, which would have deleted a **published** release. The comparisons are now `jq -e` over the JSON with typed equality, expected values passed as jq arguments rather than interpolated into a filter; the asset list's length, name characters and field types are required before any of it is parsed; a response's own text is escaped and truncated before it reaches the log, because the runner reads stdout for workflow commands. **Verified rather than assumed**: the exact `assets{?name,label}` template is what the create and get examples show and cutting at `{` is the right cut; `${var,,}` is bash 4+, ASCII-stable for these URLs, and an empty read fails the compare rather than passing it; a supplied `name` is used rather than a generated one even with `generate_release_notes=true`; `GET /releases/{id}` is documented to answer the id asked for, so checking the returned id stays; GHES answers a different upload host and path, so github.com-only exactness is a deliberate choice and not a portable claim. **Recorded where the review stops short**: it held that the two whole-string read-back comparisons were safe — true against the null-collapse it was testing, false against a documented boolean arriving as a string — so those comparisons are gone too. **Still not closed, and not closeable in YAML**: a release writer can retitle, re-tag, add or replace assets, or publish this draft between the last read and the `PATCH`, and a tag writer can move the tag afterwards; immutable releases (generally available, off by default, a repository/organization setting) lock assets and the associated tag only *after* publication, leave title, notes and prerelease/latest editable and drafts fully mutable, so cleanup must never widen to a published release — the operator decision on the board stands. **Folded, not deferred from the same round**: the case-fold covers only scheme/host/owner-repo (a differently-cased path segment from the release id on is refused); the un-draft `PATCH` response is read and checked as a release object, and its failure text does not imply nothing shipped; the run's record of what went public is printed from verified values, so no server-chosen text reaches stdout. **A trap the harness caught in the act**: `${var:0:$n,,}` is an arithmetic error in bash that does not abort a `set -euo pipefail` step, and it made six `upload_url` cases pass for the wrong reason until the expansion was split. **Counter-evidence against one suggested repair**: passing `target_commitish=<pinned sha>` on the create was proposed as a hardening and is not taken — the endpoint note makes the workflow-write guard a function of the *resolved* target relative to the default branch, and the Actions `GITHUB_TOKEN` can never satisfy that guard, so naming the tag commit turns a comparison that is empty by construction (omitted target = default-branch head) into one this run cannot evaluate before the call. What the tag commit resolves to, and what happens when it is behind the default branch, is a first-tag question, now written into §14. **Harness as of this revision**: 73 cases, 0 failures, over a stub `gh` that now builds the JSON each endpoint answers and applies the caller's own `--jq` to it the way gh does, so older revisions are shown reading the same answer through the projection they asked for. That still proves control flow and response *reading* only — nothing about GitHub's authorization decisions or the bytes on the network — and no tag has run the gate. |
| Deep review of v0.12.14 (release gate: the upload address, what a read-back identifies, what a digest proves) | **completed, dispositioned in v0.12.15**. **Fixed**: the `upload_url` compare is exact against the documented template rather than a prefix (the prefix accepted `assets-extra` and an unrequested query), case-folded so the API's canonical owner/repo spelling is not a false rejection, with uploads addressed from the returned URL; the release id and the pinned title are checked in both inspections and in the post-publish read-back. **Corrected in this SPEC**: what the stored-digest match proves (GitHub's reported digest equals the staged bytes at read time — not which call uploaded them, not what a download serves); what the scripted harness proves (the exercised mocked branches only — coverage and pass counts were not independently verified by the reviewer either); gh's token attachment is conditional, not unconditional. **Verified, not assumed**: gh v2.46.0 opens `--input` and stores the file as the request body with an explicit `Content-Length`, passes a reader straight to `http.NewRequest` (JSON marshalling happens only for map parameters), honours a supplied `Content-Type`, uses a `://` endpoint as an absolute URL, and looks the token up via hostname normalization; `${upload_url%%\{*}` is the right cut for `{?name,label}`. **Deliberately not closed**: a release writer can still retitle, re-tag, replace assets on, or publish this draft between the last read and the `PATCH`, and a tag writer can move the tag after its final resolution — more reads move the window, they do not close it; `.author.login` would add a weak creator check only if the expected principal were stable, and would not stop a later authorized writer. **New, and not a code change**: GitHub's immutable releases lock assets and the associated tag after publication and emit attestations, the strongest available control for the post-publication half of this problem; enabling it is a repository/organization setting, so it is an operator decision on the board. **Harness counts are as-of**: the earlier rows in this table quote the case counts of their own round; the harness runs 59 cases at this revision, and what it does not prove is unchanged. **One review finding was an artifact of my own prompt**: the round-7 “blocker” was Markdown link syntax wrapped around the expected URL in the request I sent — `cat -A` on the file shows a plain URL, so nothing shipped broken. |

| Deep review of v0.12.13 (release gate: what the create response proves, the upload address, the second look) | **completed, dispositioned in v0.12.14**. **Fixed**: the create response is validated as a whole (`tag_name`, `draft`, `prerelease`) before any upload; `upload_url` must name this repository and this release id; the release object's `tag_name` is checked in both inspections and in the post-publish read-back, not once before the first snapshot; the release title is pinned to the tag. **Corrected in this SPEC**: the workflow-write guard answers 404/403 rather than 422, and the same note applies to the update endpoint. **Verified, not assumed**: gh 2.46.0 passes `--input` as a raw reader with no JSON re-encoding and honours an explicit octet-stream content type, sends absolute endpoint URLs as given, and attaches the token by hostname normalization — so the upload leg is documented, and the digest match is what proves it behaved on a given run. **Deliberately not closed**: a response substitution or replay that carries a valid id and the right fields is not distinguishable by reading, so ownership rests on the create call plus tag protection and a restricted set of release writers (repository settings); reads and the un-draft `PATCH` are not atomic, and no number of reads makes them so; the generated notes body is GitHub's text, unreviewed by the gate; the workflow-write guard's behaviour for an existing tag is unknown until a run. **Harness limits, stated plainly**: 52 cases over a scripted `gh` prove this step's control flow and that it addresses the endpoints the way the step intends — they prove nothing about GitHub's authorization decisions, the real request bytes, or first-tag success. The one cheap live falsifier is a canary run on a disposable tag with the job's own token, which is an operator decision (it publishes for real for a few minutes), not a code change. |

| Deep review of v0.12.9–v0.12.12 (release gate: draft visibility, snapshot coherence, the publish race) | **completed, dispositioned in v0.12.13**. The load-bearing finding was against this SPEC's own reasoning: `GET /releases/tags/{tag}` is documented as "Get a published release with the specified tag", so the id that v0.12.11 said proved ownership could not be read for a draft at all. **Fixed**: create is `POST /releases` and the id and `upload_url` come from its response, uploads are addressed by that id, the draft is verified from one asset-list snapshot instead of five per-name lookups, and both the snapshot and the tag resolution are repeated immediately before the un-draft `PATCH`. The stub `gh` refuses the tag lookup, so v0.12.12's step is demonstrated failing rather than remembered as wrong. **Recorded rather than fixed**: the byte-size comparison is redundant once the stored digest is trusted and is kept for diagnostics; the byte fidelity of `gh api --input` (that the uploaded bytes are the staged bytes) is proven only by the stored digest matching, which is a first-tag confirmation and not a local one; a create whose response is lost leaves a draft this run cannot name — the `create_attempted` annotation and the preflight refusal are the whole story there, and re-running needs a human to remove the orphan; `if: failure()` does not run for a killed or cancelled runner, so a run that dies mid-publish has to be reconciled by hand against the releases page; a `GITHUB_TOKEN` write does not trigger downstream `on: release` workflows, so any notification keyed on publication is unverified until the first tag, as is whether creating a release for a tag whose commit touches `.github/workflows/` needs the job's token authorized to modify workflows (the endpoint's own note says it does in that case, and the least-privilege permission set is kept until a tag says otherwise). The published bytes are still never downloaded and re-hashed by the gate (the row above stands), and release-tag protection plus who may publish stays a repository-settings decision. |

| Deep review of v0.12.9–v0.12.11 (release gate: artifact forgeries, cleanup ownership, the gh CLI contract) | **completed, dispositioned in v0.12.10–v0.12.12**: four artifact forgeries (an artifact named for two legs, a regex dot standing in for `air.exe`, an extra archive member, a symlink or directory wearing an expected name) are each reproduced as a forgery that passed the shipped gate and then fixed, and are standing cases in the local gate harness; cleanup ownership moved off `github.run_started_at` (not a documented Actions context property, so read as empty it protects nothing) to the release id this run captured; and the publish path moved to REST because `gh release create` has no `--json`/`--jq` (gh 2.46.0 fails at argument parsing) and `gh release view`/`edit` resolve their operand as a tag name, not a release id. The harness's stub `gh` was what let the contract error survive two reviews: it now enforces the documented flag set. **Accepted gap, and this is the row §14's published-surface bullet points at**: the gate compares each remote asset's name, `uploaded` state, byte size and GitHub's reported stored-bytes `digest` against the staged file, but never downloads the served bytes and hashes them itself — it proves what GitHub says it stored, not what a downloader receives. Closing that fully means a post-publish download-and-hash leg or a signing/attestation story, which is runner minutes plus a trust-anchor decision rather than a constant; the published per-archive sidecars and `SHA256SUMS` remain what a downloader verifies. **Deliberately not closed**: workspace-internal link tricks above the leg folder, PAX extension handling and decompression bounds in the archive inspection, and the window between re-resolving the tag and creating the release — `concurrency: release-${{ github.ref }}` serializes this repo's own runs, but no workflow line excludes an external actor with write access, so release-tag protection and who may publish are repository-settings decisions for the operator. |

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
