# Arboreal Inference Router

An OpenAI-compatible HTTP front door for several local LLM inference engines. It queues, prioritises
and routes requests, and switches which model an engine serves when a request needs one that is not
loaded. Configured entirely from TOML/JSON, shipped as a Native-AOT single executable, with no
reflection.

![CI](https://github.com/ArborealML/Inference-Router/actions/workflows/ci.yml/badge.svg?branch=main)
![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)
[![.NET](https://img.shields.io/badge/.NET-11.0-purple)](https://learn.microsoft.com/dotnet/)

It exists because a single GPU box running one model at a time is a scheduling problem: several
agents want different models, some need a bigger context window than the one currently loaded, and
switching a backend is slow and disruptive. The router holds the requests, decides which engine
should serve them, and switches engines only when it has to.

## Feature status

| Works today | Not built yet |
| --- | --- |
| OpenAI-compatible chat/completion proxying, streaming included | Ingress authentication and TLS termination |
| Per-model queues with priority classes and tie-breaks | Router-host / listener split, multi-host routing |
| Context-window escalation across serving profiles | Global impact-aware scheduling, suffix-cache reuse |
| Engine and model switching driven by config (lifecycle commands) | Live web dashboard |
| Health probes, capacity signals from the engine | Config reload without restart |
| Admin surface: status, events (SSE replay), threads, switch, tickets | Published release binaries (first tag pending) |

Early-stage internal tool. 92 tests, build clean under `TreatWarningsAsErrors`, `PublishAot` with
zero trim warnings. What is designed but unimplemented is specified in
[`SPEC.md`](SPEC.md) §12–§13, not guessed here.

## Security and trust model

- **Loopback by default**: `listen = "http://127.0.0.1:8321"`. Change it deliberately.
- **No ingress authentication yet**, and requests are plaintext HTTP — the router does not terminate
  TLS. Any caller that can reach the port can claim any thread priority and reach `/admin/*`. The
  deployment posture is loopback, or behind your own proxy that authenticates and terminates TLS.
- **Engine credentials** are per engine: `api_key` inline, or `api_key_file` pointing at a file read
  at startup. Keep key files out of the repository.
- **Telemetry holds no prompt text.** Events carry sequence, timestamp, kind, thread id, ticket id,
  engine, model, reason and a numeric value. Prompt bodies are forwarded to engines and back to
  callers; they are not journalled.
- **Engine failures are proxied, not sanitised**: an upstream 4xx/5xx body reaches your client inside
  an `engine_error` envelope, so engine-side detail (including paths and model ids) is visible.
- Lifecycle commands run as the router's user. There is no container identity check yet
  (`SPEC.md` §15.1).

## What it does

- **OpenAI-compatible passthrough** — `POST /v1/chat/completions`, `POST /v1/completions`
  (including streaming), `GET /v1/models`. Upstream bodies are forwarded as bytes; the
  router never re-serialises tokens.
- **Chat-thread priority** — requests carry a thread id (`X-Inference-Thread`, or a body
  field); each thread has a priority class (`background … interactive`) plus a 0–255
  tie-break. Interactive work preempts queued background work. These are conversation
  threads, not OS threads.
- **Cross-model queues** — one queue per model; a request for a model that is not loaded
  is held, not failed, and dispatched when an engine can serve it.
- **Engine and model switching from config** — each model can declare a lifecycle command
  (on this machine: `select-model`) that the router runs to change what an engine serves,
  then waits for the engine to report healthy. Startup/shutdown support, timeouts, cooldowns
  and health gating are config, not code.
- **Serving profiles / context-window escalation** — a model declares tiers such as
  `98k/4 slots`, `128k/2`, `1m/1`. A request that needs more context than the active window
  is queued behind a switch to the smallest tier that fits, instead of forcing the caller to
  compact. Engines whose ladder cannot fit the request are skipped.
- **Capacity signals** — admission is `min(router ceiling, slots the engine reports)`, read
  from the same status probe that drives health, so an engine advertising 2 slots does not
  get 8 concurrent requests.
- **Operability** — `/admin/status`, `/admin/events` (replayable SSE), `/admin/threads`,
  `/admin/switch`, `/admin/tickets/{id}` (read and cancel), `/healthz`; a `config` verb that
  validates a config file and prints the resolved plan without binding sockets or running
  lifecycle commands.

## Install

Requires the .NET 11 SDK (`global.json` pins `11.0.100`, `rollForward: latestFeature`). Native AOT
also needs a C toolchain on the build machine — clang/LLVM on Linux, MSVC on Windows.

There are no published binaries yet — the first tag will produce `air` archives for `linux-x64`,
`win-x64`, `osx-arm64` and `osx-x64` with `SHA256SUMS` (see **CI and releases**). Until then, build
it:

```bash
git clone https://github.com/ArborealML/Inference-Router.git && cd Inference-Router
dotnet build
dotnet test

# single-file native binary (named `air`)
dotnet publish src/Arboreal.InferenceRouter.Cli -c Release -r linux-x64 \
  /p:PublishAot=true /p:StripSymbols=true /p:DebugSymbols=false /p:DebugType=None
```

The server project publishes the same router as an ASP.NET host:

```bash
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
```

## Quick start

```bash
# validate a config and print the resolved engine/model/serving plan (safe: no sockets)
dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c samples/router.toml

# run the server
dotnet run --project src/Arboreal.InferenceRouter.Server -- -c samples/router.toml

# talk to it
curl -s localhost:8321/v1/models
curl -s localhost:8321/v1/chat/completions \
  -H 'content-type: application/json' \
  -H 'X-Inference-Thread: agent-42' -H 'X-Inference-Priority: interactive' \
  -d '{"model":"qwen-3.8-flash-next","messages":[{"role":"user","content":"hi"}]}'
```

`samples/router.toml` is a reference config modelled on one developer's machine — the
endpoints, model ids, serving tiers and key paths are examples, not defaults. Point it at
your own engines. `samples/router.safe-smoke.toml` is an inert config (engines marked
`remote`, no lifecycle commands) for testing without starting or stopping anything.
`samples/router.ci-smoke.toml` is the self-contained config the smoke script uses.

## Verify it works

Against a real config, in this order — each step isolates a different layer:

```bash
air config -c your.toml          # config loads, ladder and probes resolve
curl -s localhost:8321/healthz   # the router is listening
curl -s localhost:8321/admin/status | head -c 400   # engines healthy? models loaded? slots?
curl -s localhost:8321/v1/models                    # what the engines report right now
```

`/admin/status` reports each engine's `healthy`, `loaded` models, active profile and slot count; an
engine listed unhealthy is a probe problem, not a routing problem. To watch decisions as they happen:
`curl -N localhost:8321/admin/events`. A built binary can be exercised end-to-end against a fake
engine, with no GPU, keys or network:

```bash
tools/ci/smoke.sh ./src/Arboreal.InferenceRouter.Cli/bin/Release/net11.0/linux-x64/publish/air
```

Every router failure is one lowercase body — `{"error":{"type":"air_error","code":…,"message":…}}` —
and the code tells you where to look:

| Code | Status | Usual cause |
| --- | --- | --- |
| `invalid_request` | 400 | Malformed body or header, rejected before the engine sees it |
| `not_found` | 404 | Unknown route, or a model no engine's catalogue declares |
| `deadline_exceeded` | 408 | `max_queue_time` elapsed while the request waited |
| `cancelled` | 499 | Caller hung up or `DELETE /admin/tickets/{id}` |
| `queue_overflow` | 429 | Per-model queue full: back off, or raise `max_queue_per_model` |
| `engine_error` / `engine_unreachable` | 502 | The engine answered wrongly, or is not answering; its own message is embedded |
| `switch_failed` / `model_unavailable` | 503 | Lifecycle switch failed, or no engine can serve that model |
| `no_available_engine` | 500 | Nothing eligible: check `/admin/status` for slots and health |

The same table, with the agent-facing headers and the worked "the log is too big" session, is in
[`docs/ai-integration.md`](docs/ai-integration.md).

## Configuration shape

```toml
[engine.strata]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18021/v1"
api_key_file = "/etc/arboreal/keys/strata-api-key"
health_probe = { path = "/props", expect = "total_slots>0", context_path = "n_ctx", slots_path = "total_slots" }
# Request paths join onto `endpoint` by one rule: a leading '/' is absolute from the server root
# ("/props", "/models"), a bare name appends to the endpoint path. Completion calls always append,
# so an engine mounted at …/v1 is called at /v1/chat/completions.

[engine.strata.catalog."qwen-3.8-flash-next"]     # dotted ids MUST be quoted (TOML tables)
default_profile = "128k"
[[engine.strata.catalog."qwen-3.8-flash-next".serving]]
name = "128k"
max_context = 131072
max_slots = 8
select = { exec = "select-model", args = ["{{model}}", "--profile", "{{profile}}"], wait_health = true }
```

The same schema loads from JSON. Deserialisation uses Tomlyn and System.Text.Json **source
generators**; there is no runtime reflection, and the build bans reflection-only APIs.

## From an agent (Pi, or anything that speaks HTTP)

The router is a normal HTTP server, so an agent needs no SDK. The two things worth knowing beyond
the OpenAI request shape are thread identity and context demand:

```bash
curl -N http://127.0.0.1:8321/v1/chat/completions \
  -H 'content-type: application/json' \
  -H 'X-Inference-Thread: claude-session-7' \
  -H 'X-Inference-Required-Context: 24000' \
  -d '{"model":"qwen-3.8-flash-next","stream":true,"messages":[{"role":"user","content":"hi"}]}'
```

`X-Inference-Thread` gives the conversation its share of the queue — its priority class is set with
`POST /admin/threads` and holds for every later request that carries the header. Note that the
router does not authenticate callers yet, so priority claims are honoured as given: keep the router
on loopback or behind your own proxy. `X-Inference-Required-Context` tells the scheduler the window
you need, so it switches profile instead of failing. While a request waits or a model loads, the
stream carries `data: [router keepalive …]` frames rather than going silent, and a failed stream
ends with an error frame plus `[DONE]`.

[To follow one request you own — including why it is waiting — read only its events:
`GET /admin/tickets/<id>/events?since=0`, where `<id>` came back in the `X-Inference-Ticket` header.
Other filters on the global feed: `kinds=`, `thread=`, `engine=`, `ticket=`.

[`samples/pi/inference-router-tools.json`](samples/pi/inference-router-tools.json)
describes the same calls as tool definitions for a harness. Nothing in the router reads that file.

## CI and releases

`ci.yml` builds (warnings are errors), runs the tests and performs a trim-warning-free Native-AOT
publish on every push to `main` and every pull request.

`release.yml` runs on a tag such as `v0.1.0`. It first refuses the tag if its format is wrong, it
does not resolve to a commit, or **any release (draft or published) already uses it** — that check
runs before any build. It then builds a stripped Native-AOT `air` for `linux-x64`, `win-x64`,
`osx-arm64` and `osx-x64` on native runners, smoke-tests each binary on its own operating system
(`tools/ci/smoke.sh`), and only then creates the release: a draft with the four archives and
`SHA256SUMS`, published once every asset is uploaded. A failed publish removes the draft that run
created. To exercise the whole pipeline without releasing, run the workflow manually with
`dry_run` checked.

## Layout

| Path | What lives there |
| --- | --- |
| `src/Arboreal.InferenceRouter/` | library: `RouterCore`, priority, queues, scheduler, prefix trie, engine runtime, config loader, event ring |
| `src/Arboreal.InferenceRouter.Server/` | HTTP surface (Minimal APIs, AOT JSON context) |
| `src/Arboreal.InferenceRouter.Cli/` | `serve`, `chat`, `models`, `status`, `events`, `threads`, `switch`, `tickets`, `config` |
| `tests/` | xUnit suite, including real-HTTP wire tests and shell round-trip tests for lifecycle argv |
| `bench/` | JIT overhead harness: engine → routing → HTTP ladder against one shared fake engine |
| `docs/benchmarks.md` | measured overhead, what the numbers exclude, and the bug the harness caught |
| `docs/ai-integration.md` | how an LLM agent (Pi or any HTTP client) should drive the router |
| `samples/` | reference and inert configs; `samples/pi/` holds the tool manifest for agent harnesses |
| `tools/ci/` | the smoke script every release leg runs |
| `backlog/` | planned and in-flight work as Markdown tasks |
| `groma/` | architecture map: components, flows, relationships |
| `SPEC.md` | specification and revision log |

## Roadmap (spec'd, not built)

Router **host** + separate **listener** service · multi-host routing that prefers engines
already holding a shared prefix · global scheduling that accounts for the cost one request's
prefill imposes on the others · suffix-cache reuse · a Blazor dashboard with live queue,
engine and per-request state. Details and open questions: SPEC §12, §13, §15.

## Working in the repo

Planned work lives as Markdown tasks under `backlog/`, and the architecture map lives under
`groma/` — both are plain files reviewed alongside the code. The two CLIs are `backlog` and
`groma`; `AGENTS.md` says how agents use them.

### Benchmarks

Performance claims are gated on measurement, not assertion:

```sh
dotnet run -c Release --project bench/Arboreal.InferenceRouter.Benchmarks -- 400 60
```

The harness runs the same in-process fake engine behind every layer, so each row's difference
is router work. Latest run (.NET 11.0.0, Ubuntu 26.04.1, 24 logical cores, workstation GC): routing adds ~58–91 µs p50 and ~15 KB per
request (flat from concurrency 4 to 16); the full HTTP path ~217–996 µs and ~26–32 KB — the top
of that range is HTTP framing saturated, not routing; the SSE relay ~58–102 µs and ~25 KB. All
figures, caveats, and what is still unmeasured: [`docs/benchmarks.md`](docs/benchmarks.md).

## License

MIT — see [LICENSE](LICENSE).
