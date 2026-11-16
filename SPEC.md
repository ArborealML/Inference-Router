# Arboreal Inference Router — Specification

Status: draft, continuously revised (v0.4)
Target: .NET 11 (10 acceptable; 11 SDK 11.0.100 installed), Native AOT everywhere.
Repository: `~/src/arboreal/inference-router` (git, this repo).

## 0. Revision log

- v0.1 — initial architecture + scope.
- v0.2 — added TOML config schema, engine lifecycle contract, thread model, AOT strategy, research questions.
- v0.3 — tightened scheduling semantics (prefix-aware scoring, prefill impact), added suffix-cache forward design, observability events, acceptance criteria, repo layout; recorded the research questions and their disposition.
- v0.4 — ASP.NET-under-Native-AOT research folded into §9 (RDG, CreateSlimBuilder, STJ mode guidance, SSE via TypedResults.ServerSentEvents, HttpClient type-info overloads, trim-warning discipline); added §13 Pi integration (mid-session model switching, intelligent wait protocol, design pass dispatched as a deep research question).

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
  refuses if a switch is in flight unless `force`.
- `GET /admin/status` — engines, loaded models, queue depths, in-flight,
  stats per thread/model/engine.
- `GET /admin/events?since=<seq>` — SSE feed of `RouterEvent`s (see §10),
  `text/event-stream`, replayable from ring buffer by sequence.
- `GET /admin/tickets/{id}`, `DELETE /admin/tickets/{id}` (cancel).
- `POST /admin/queue` — future listener/host enqueue path (spec'd but
  hostless in v1; returns 501 until §12.1 ships).

**Threading through HTTP**: clients convey thread + priority via headers:
`X-Inference-Thread: agent-42`, `X-Inference-Priority: high:200`
(`class[:sub]`), `X-Inference-Affinity: <normalized-prefix-hash>` (optional,
server-side prefix normalization is authoritative anyway). Body-only callers
(default thread `anon`, priority Normal) work unchanged.

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

[engine.strata.catalog.qwen-3.8-flash-next]
aliases = ["flash-next", "default"]
profiles = ["max", "coder"]
select = { exec = "select-model", args = ["{{model}}", "--profile", "{{profile}}"],
           wait_health = true, wait_timeout_ms = 7200000 }

[engine.strata.catalog.qwen-3.8-flash-next-coder]
aliases = ["flash-next-coder", "coder"]
profiles = ["max-coder"]
select = { exec = "select-model", args = ["{{model}}"], wait_health = true }

[engine.hyperqwen]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18020/v1"
api_key_file = "/etc/arboreal/keys/hyperqwen-api-key"
capabilities = { vision = false, suffix_cache = true, max_context = 131072 }
[engine.hyperqwen.catalog.qwen-3.8-27b]
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
  for all three shipping projects must exit 0 with **zero trim warnings**.

## 10. Observability

`RouterEvent` ring buffer (per-host, ordered, monotonic `Seq`, replayable):

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
```

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

### 12.3 Global impact-aware scheduling
`SchedulerView.Engines` gains `remotePrefillCost` (uncached prefill stalls
others) — the β term in §5.4 becomes per-engine, learned from observed TTFT
regressions (EWMA), no interface change.

### 12.4 Suffix-cache scheduling
Capability-gated scorer (§5.4); engines advertise `suffix_cache` + extent
query; scheduler prefers reuse-capable engines with matching suffix extents
subject to the fork's ordering constraints (unspecified here, by design).

### 12.5 Blazor web client
Reads `/admin/events` (SSE) + `/admin/stats`; static SSR + selective
Interactive Server per T011 guidance; never bypasses the public API. Live
view = request log stream + per-engine gauges + queue depths.

## 13. Pi integration (session-scoped model switching)

Pi (the coding agent harness) is a first-class client of AIR. Goals:
(1) Pi talks to AIR as an OpenAI-compatible provider; (2) **mid-session
model switching** — the same Pi session can move between AIR models;
(3) **intelligent wait** — AIR loading a model for 1–15 min must not kill
Pi's request. Design pass R5 (the research service, deepest tier) is pending; the committed
constraints it must respect:

- **Provider shape**: models.json-compatible endpoint is the baseline
  (`baseUrl: http://127.0.0.1:8321/v1`, `api: "openai-completions"`);
  a Pi **extension** (`registerProvider` + `refreshModels`) is the
  mechanism for dynamic catalogs and switch UX. Pi's `/model` reloads
  models.json — acceptable fallback, not the primary path.
- **Thread mapping**: Pi session id → AIR thread id via
  `X-Inference-Thread`; priority via `X-Inference-Priority` (config per
  Pi model entry).
- **Wait protocol**: AIR streams SSE keepalive comment frames
  (`: keepalive`) plus a *status chunk* signaling "model switching"
  before real deltas, so Pi's idle timeout (`httpIdleTimeoutMs`, default
  300 s) never fires; non-streaming paths use `Retry-After` + a documented
  retryable error code. Final protocol decided by R5.
- **AIR additions required**: `GET /admin/status` exposes `switching:{engine,model,etaMs}`;
  events feed carries `engine.switch_*` (already in §10); `/v1/models`
  marks switching models with a `status` extension field.

## 14. Testing & acceptance

- **Unit**: priority comparability, queue ordering/overflow, scheduler
  scoring (affinity vs busy), TOML binding (valid/invalid/unknown kinds),
  prefix trie match/eviction, DTO round-trips.
- **Integration** (in-memory fake engine serving recorded OpenAI SSE):
  end-to-end enqueue → select → stream → complete; cancellation;
  switch-while-queued (request waits for model load); health probe failure.
- **AOT**: full AOT publish of Server + Cli with zero trim warnings on CI.
- **Compat test**: `curl` with stock `openai` python client against the
  server (chat, stream, models) — must pass unchanged.

## 15. Research questions and dispositions

| Question | Disposition |
|---|---|
| Cysharp-style performance guidance for this codebase | pending |
| AOT-safe TOML: CsToml vs Tomlyn on .NET 11 | pending |
| ASP.NET minimal APIs under Native AOT on .NET 11; STJ source-gen for streaming | pending |
| Prefix/suffix-aware scheduling prior art (SGLang router, Llumnix, vLLM) | pending |
| Pi mid-session model switching + wait protocol design | pending |

Findings feed §7 (TOML binding choice), §9 (AOT strategy), §5.4 (scheduler
heuristics). Spec revisions cite them.

## 16. Repository layout

```
SPEC.md, README.md, AGENTS.md (build/test conventions)
dotnet-tool-manifest? no — Cli ships as normal tool
src/Arboreal.InferenceRouter/{AirJson.cs, ...}
src/Arboreal.InferenceRouter.Server/
src/Arboreal.InferenceRouter.Cli/
tests/Arboreal.InferenceRouter.Tests/
samples/router.toml
```
