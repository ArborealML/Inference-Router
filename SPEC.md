# Arboreal Inference Router — Specification

Status: draft, continuously revised (v0.8.8)
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

#### 5.3.1 Container-native lifecycle (in design)

Shell-outs to a launcher script are an integration cost, not a feature: they hide the
state the router needs, and a failed script leaves an orphaned container the scheduler
cannot see. The target design drives the container runtime directly.

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
429, `engine_error` / `engine_unreachable` 502, `switch_failed` / `model_unavailable` 503,
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
Subscribers get a bounded buffer (256 events): if a consumer falls behind, writes are dropped and
the missing `Seq` numbers are visible to it, rather than the router holding unbounded memory for a
dead client. Field names on the wire are PascalCase (§6).

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
