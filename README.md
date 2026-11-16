# Arboreal Inference Router

A .NET Native-AOT LLM **inference router**: one OpenAI-compatible front door in front of
several local inference engines, with request prioritisation, model/context-window
auto-switching, and queueing — configured entirely from TOML/JSON, with no reflection.

It exists because a single GPU box running one model at a time is a scheduling problem:
several agents want different models, some need a bigger context window than the one
currently loaded, and switching a backend is slow and disruptive. The router holds the
requests, decides which engine should serve them, and switches engines only when it has to.

Library (`src/Arboreal.InferenceRouter`), server
(`src/Arboreal.InferenceRouter.Server`), CLI (`src/Arboreal.InferenceRouter.Cli`).
Design and decisions live in [`SPEC.md`](SPEC.md); the README is the entry point.

## Status

Working and tested (64 tests, warning-free build under `TreatWarningsAsErrors`, clean
`PublishAot` with zero trim warnings). This is an early-stage internal tool: the request
path, priority queue, scheduler, config and lifecycle handling are implemented; the
router-host/listener split, multi-host scheduling and the live web dashboard are
spec'd but not built (SPEC §12–§13).

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
  `/admin/switch`, `/admin/tickets/{id}`, `/healthz`; a `config` verb that validates a config
  file and prints the resolved plan without binding sockets or running lifecycle commands.

## Quick start

```bash
dotnet build                                    # solution: inference-router.slnx
dotnet test

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

Native AOT:

```bash
dotnet publish src/Arboreal.InferenceRouter.Server -c Release -r linux-x64 /p:PublishAot=true
```

`samples/router.toml` is a reference config modelled on one developer's machine — the
endpoints, model ids, serving tiers and key paths are examples, not defaults. Point it at
your own engines. `samples/router.safe-smoke.toml` is an inert config (engines marked
`remote`, no lifecycle commands) for testing without starting or stopping anything.

## Configuration shape

```toml
[engine.strata]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:18021/v1"
api_key_file = "/etc/arboreal/keys/strata-api-key"
health_probe = { path = "/props", expect = "total_slots>0", context_path = "n_ctx", slots_path = "total_slots" }

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

## Layout

| Path | What lives there |
| --- | --- |
| `src/Arboreal.InferenceRouter/` | library: `RouterCore`, priority, queues, scheduler, prefix trie, engine runtime, config loader, event ring |
| `src/Arboreal.InferenceRouter.Server/` | HTTP surface (Minimal APIs, AOT JSON context) |
| `src/Arboreal.InferenceRouter.Cli/` | `serve`, `chat`, `models`, `status`, `events`, `threads`, `switch`, `tickets`, `config` |
| `tests/` | xUnit suite, including real-HTTP wire tests and shell round-trip tests for lifecycle argv |
| `SPEC.md` | specification and revision log |

## Roadmap (spec'd, not built)

Router **host** + separate **listener** service · multi-host routing that prefers engines
already holding a shared prefix · global scheduling that accounts for the cost one request's
prefill imposes on the others · suffix-cache reuse · a Blazor dashboard with live queue,
engine and per-request state. Details and open questions: SPEC §12, §13, §15.

## License

MIT — see [LICENSE](LICENSE).
