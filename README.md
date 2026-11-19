![AIR — Arboreal Inference Router](docs/images/air-banner.svg)

# Arboreal Inference Router

[![CI](https://github.com/ArborealML/Inference-Router/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/ArborealML/Inference-Router/actions/workflows/ci.yml)
[![MIT license](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)
[![.NET 11](https://img.shields.io/badge/.NET-11.0-purple)](global.json)

AIR gives OpenAI-compatible clients one HTTP endpoint for local inference engines. When several conversations share a GPU, AIR queues requests by model, gives interactive threads priority over queued background work, and uses engine health and available slots to decide what can run. Clients can request a context window and stream the reply through the same endpoint.

## Try it without a GPU

From a checkout, use the .NET 11 SDK, Bash, Python 3 and curl to run a chat through the router and a local fixture engine. The script starts both processes on loopback and stops them on exit. It needs ports 18123 and 18321 free.

```bash
dotnet build src/Arboreal.InferenceRouter.Cli -c Release
bash tools/ci/smoke.sh ./src/Arboreal.InferenceRouter.Cli/bin/Release/net11.0/air
```

On Windows, use Git Bash and pass the executable path ending in `air.exe`. A successful run ends with `SMOKE OK`. It checks model discovery, ordinary and streaming chat, the CLI client, and the error returned when the engine fails. If the executable reports that .NET is missing despite a successful build, set `DOTNET_ROOT` to the directory containing your installed `dotnet` runtime and rerun the script.

## Features

- **OpenAI-compatible inference.** Send chat or legacy text completions with optional SSE streaming. [Client integration guide](docs/ai-integration.md)
- **Model discovery.** `GET /v1/models` lists each engine’s last accepted roster and can include observed context windows in tokens. [Model discovery contract](docs/ai-integration.md#discover-loaded-models)
- **Routing and serving profiles.** Queue requests by model, apply thread priorities, and route to configured local and remote engines using health and available slots. The built-in scheduler checks explicit context minimums and required serving profiles against the window observed for that model. [Serving profiles specification](SPEC.md#76-serving-profiles-and-context-window-escalation)
- **Per-engine lifecycle authority.** Set `lifecycle_owner` independently of the inference endpoint. Externally managed engines forward loaded models. A dedicated local engine can use the native Docker provider, and a delegated engine can use a configured selector. A verified refusal before any mutation can leave the incumbent available to serve requests. [Lifecycle configuration](SPEC.md#7-configuration-toml)
- **Measured switch ordering.** On a single idle native Docker engine with a proven incumbent, AIR can prefer the cheaper of eligible queued model switches. Estimates use recent observed shutdown and activation times. Queue age takes precedence within the comparison, and incomplete estimates fall back to enqueue order. [Queue semantics](SPEC.md#55-queue-semantics)
- **Operator controls.** Inspect engine and queue status, replay events for a ticket, or cancel it through the admin API. The `air` CLI can host the router and act as a client. [HTTP reference](SPEC.md#6-http-surface-server-project)

### In progress

- **Context fit at dispatch.** Carry the selected ticket’s capacity check through dispatch and finish switch restoration and deadline verification. [Serving gauge verification](backlog/tasks/task-68%20-%20Bind-the-serving-gauges-to-a-capability-epoch-so-a-stale-window-cannot-authorise-a-profile-the-engine-cannot-serve.md)
- **Native model swaps on a shared host.** Fake controllers exercise reservation and readiness, and an offline sender exercises exact-ticket admission and cleanup. Production swaps still require an enforced residency partition and instance-bound admission through inference completion. [Native swap milestone](backlog/milestones/m-4%20-%20native-model-swap-through-pi-clm.md)
- **Native Docker launch configuration.** Validate GPU selection, bind mounts, container limits and per-profile environment, mounts and memory limits offline. Resource-bearing switches require host authority, effective-resource inspection and verified serving instances before activation. [Docker resource configuration](SPEC.md#7-configuration-toml)
- **Lifecycle on managed peers.** Starting and stopping models on a peer still needs host-wide fencing that accounts for foreign occupancy and stays in force through readiness. Scheduling preference among loaded and switchable engines is also in progress. [Lifecycle ownership plan](backlog/tasks/task-77%20-%20Split-one-remote-flag-into-transport-reach-and-model-loading-ownership.md)
- **Instance-bound upstream transport.** Offline tests send one request over an authenticated stream issued for the verified serving instance. Production integration still needs request and authorization binding, durable issuance and verified retirement. [Transport specification](SPEC.md#531-container-native-lifecycle-partly-implemented)

### Planned features

- **Cluster routing and model placement.** Accept requests from either ingress into one shared queue and let an eligible host activate the selected model. [Cluster milestone](backlog/milestones/m-6%20-%20cluster-routing-and-model-placement.md)
- **Batching and switch-rate limits.** Use measured costs and queue age to serve cohorts and bound repeated switches. [Scheduling milestone](backlog/milestones/m-5%20-%20measured-model-scheduling.md)
- **Configuration reload.** Apply validated changes while in-flight requests and switches keep the configuration they started with. [Reload milestone](backlog/milestones/m-8%20-%20configuration-reload-during-inference.md)
- **Operator live view.** Follow engine status and per-ticket progress in a web interface backed by the replayable event feed. [Live view milestone](backlog/milestones/m-11%20-%20operator-live-view.md)

See the [delivery milestones](backlog/milestones/README.md) for the full roadmap.

## Connect an inference engine

Create the ignored directory with `mkdir -p .local` and save this configuration as `.local/router.toml`. Replace the example endpoint and both occurrences of `example-model` with your backend’s base URL and model ID. Set `models_endpoint` to its model-list path. A leading slash resolves from the backend’s root, so an OpenAI-compatible engine mounted at `/v1` typically uses `/v1/models`.

```toml
listen = "http://127.0.0.1:8399"
default_model = "example-model"
auto_switch = false

[engine.local]
kind = "openai-compatible"
endpoint = "http://127.0.0.1:8000/v1"
models_endpoint = "/v1/models"
lifecycle_owner = "external"

[engine.local.catalog."example-model"]
```

With `lifecycle_owner = "external"`, another service must have the model loaded before AIR can route to it. Legacy `remote = true` still gives the same forward-only behavior. For multiple engines and serving profiles, see the [remote-only sample](samples/router.safe-smoke.toml). Keep the router on loopback unless a proxy authenticates callers and terminates TLS.

```bash
dotnet run --project src/Arboreal.InferenceRouter.Cli -- config -c "$PWD/.local/router.toml"
dotnet run --project src/Arboreal.InferenceRouter.Cli -- serve -c "$PWD/.local/router.toml"
```

The first command validates the configuration offline. Its output includes resolved endpoints, key-file paths and lifecycle arguments, so keep it local. The second command starts AIR. From another terminal, check the listener and see which models the engines currently report.

```bash
curl -fsS http://127.0.0.1:8399/healthz
curl -fsS http://127.0.0.1:8399/v1/models
```

Point your client’s OpenAI base URL at `http://127.0.0.1:8399/v1`. To send a streamed chat, replace `MODEL_ID` with an ID from the list.

```bash
curl -N http://127.0.0.1:8399/v1/chat/completions \
  -H 'Content-Type: application/json' \
  -H 'X-Inference-Thread: example-session' \
  -d '{"model":"MODEL_ID","stream":true,"messages":[{"role":"user","content":"Hello"}]}'
```

Reuse the thread ID to identify the conversation across turns. The default class is `normal` in this example. Register a thread priority through the admin API or set `X-Inference-Priority` on a request. If a client knows its context demand, it can send `X-Inference-Required-Context` in tokens so AIR can consider a fitting serving profile. Queued streams carry SSE keepalive comments until data is available. AIR also supports the legacy `/v1/completions` endpoint. See the [client integration guide](docs/ai-integration.md) for headers, cancellation, error codes and a worked example.

## Model switches and serving profiles

An engine’s catalog maps model IDs and aliases to serving profiles. Each profile declares a context window and slot count. The scheduler considers those profiles alongside capacity reported by the backend. A larger context window can mean fewer concurrent requests.

For router-managed engines, the native Docker provider checks ownership labels and verifies the image ID, named repository digest, and platform before stopping an owned container. Create uses the pinned image ID. Existing tag-only configurations still load. Add all three pins before using native switching. A local-only image with no repository digest cannot pass verification. Switches serialize per engine. Native lifecycle verification uses a fake daemon, and host-wide exclusion and verified recovery are still under development. [Lifecycle and configuration specification](SPEC.md#531-container-native-lifecycle-partly-implemented)

Legacy Docker configurations can continue using their existing daemon transports and per-engine ownership checks, including in a router with multiple engines. These settings provide no host-wide occupancy proof. Fenced lifecycle on a managed peer requires an enrolled host controller. [Lifecycle configuration](SPEC.md#7-configuration-toml)

With legacy configuration, omitting `remote` or setting it to `false` without a Docker provider, an explicit `lifecycle = "exec"`, or a usable selector gives AIR no lifecycle authority. An attempted switch now returns `no_lifecycle_owner` instead of `no_select_command`. Loaded models remain routable. An explicitly delegated engine with a missing target selector still returns `no_select_command`. [Migration rules](SPEC.md#7-configuration-toml)

The [native Docker sample](samples/router.native-docker.toml) uses illustrative pins. The [reference configuration](samples/router.toml) shows the legacy external selector. Converting all selector profiles is tracked in TASK-111. Running a lifecycle-enabled configuration can start or stop real backends. Use `air config` for offline inspection and the [remote-only sample](samples/router.safe-smoke.toml) for smoke tests. TOML and JSON use the same schema.

## Inspect a running router

| Question | Request |
| --- | --- |
| Is the router listening? | `GET /healthz` |
| Which engines are healthy, and what is loaded? | `GET /admin/status` |
| Why is a request waiting? | `GET /admin/tickets/{id}/events?since=0`, using the response’s `X-Inference-Ticket` |
| What is changing across engines and queues? | `GET /admin/events` for the replayable SSE feed |

Anyone who can reach AIR can call the admin endpoints and claim a thread priority. Engine credentials are configured separately for each backend. Router events omit prompt text, but upstream error bodies can expose backend details to clients.

## Builds and releases

Build from source for current development. The release workflow targets Linux x64 and Windows x64. The [canary prerelease](https://github.com/ArborealML/Inference-Router/releases/tag/v0.0.10-canary) contains both archives and their SHA-256 checksums. macOS builds are local. To publish a standalone Linux executable with a native toolchain installed, run:

```bash
dotnet publish src/Arboreal.InferenceRouter.Cli -c Release -r linux-x64 -p:PublishAot=true
```

[CI](.github/workflows/ci.yml) builds the solution, runs the tests and checks Native AOT publication. The [release workflow](.github/workflows/release.yml) smoke-tests binaries on their target platforms and verifies draft assets before publishing.

## Development

Run `dotnet test` from the repository root. The [routing library](src/Arboreal.InferenceRouter/) owns queues, scheduling and engine state. The [server](src/Arboreal.InferenceRouter.Server/) exposes the HTTP API, and the [CLI](src/Arboreal.InferenceRouter.Cli/) can host the server or act as a client.

- [Specification](SPEC.md) — wire contracts, scheduling behavior and configuration.
- [Benchmarks](docs/benchmarks.md) — routing and streaming overhead measured against the same fixture engine, with latency and allocation breakdowns.
- [Contributor guide](AGENTS.md) — focused tests, worktrees and the review process. Work is tracked in the [backlog](backlog/).

Released under the [MIT license](LICENSE).
