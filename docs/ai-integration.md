# Calling the router from an agent

The router is a plain HTTP server with an OpenAI-compatible request path, so any agent that can
POST JSON can use it. What it adds over pointing an agent straight at one engine:

- **A URL that survives a model switch.** When the router loads a different model on the same
  engine, your base URL and model list keep working; you do not have to know which engine is live.
- **Context-aware scheduling and bounded queues.** The scheduler accepts a context requirement in
  tokens. If the queue is full, the router returns `429`.
- **One place to watch what happened.** Every decision lands on a bounded event ring: `Seq`,
  `Timestamp`, `Kind`, and whichever of `ThreadId`, `TicketId`, `Engine`, `Model`, `Reason`, `Value`
  apply (admin JSON is PascalCase, and `EventSurfaceTests` pins it). `Seq` is monotonic, and the
  feed tells you when it can no longer keep up: the ring keeps the last 4096 events, `since=` is
  exclusive and replays everything still retained for what you asked for, and a slow SSE subscriber
  gets its stream **ended** rather than silently pruned — reconnect with the last `Seq` you saw.
  Nothing yet reports how many events were missed; that is a planned event kind, not a current one. Kinds you will see on a healthy box: `request.enqueued`, `request.selected`,
  `request.streaming_first_token`, `request.completed`, `queue.rejected`, `engine.switch_started`,
  `engine.switch_completed`, `engine.switch_failed`, `engine.health`, `engine.profile`,
  `thread.priority_changed`.

## Start it

```bash
air serve --config samples/router.local.toml        # or samples/router.toml for the multi-engine setup
```

The front door is `http://127.0.0.1:8321` (override with `--listen`); health is `GET /healthz`
(`ok`). The router does not authenticate its own callers — put it behind whatever boundary you
already have, or keep it on loopback. The `air` binary is also a client: `air chat`, `air models`,
`air status`, `air events`, `air threads`, `air switch`, `air tickets`. If your harness can shell
out but not speak HTTP, drive it through those.

## Discover loaded models

`GET /v1/models` lists each engine’s last accepted roster. With `loaded_requires_status_loaded = true`,
AIR lists only rows whose `status.value` is `"loaded"` and whose `alias_of` is absent or `null`.
With the default `false`, AIR lists every upstream row with a string `id`, including aliases and
rows without load status. When the previous and new observations both lack verified windows,
AIR can keep the previous spelling if only letter case changes.

On a healthy engine with no switch in progress, a row can include `context_window`, a positive
integer in tokens observed for that model on that engine. If the upstream reports effective
serving context per model, set `models_context_window_path` to the dotted path inside that row.
AIR omits the field when it has no usable window for the row, including for aliases. If several
engines report the same model ID, `owned_by` identifies the engine for each row and each can
report a different window.

A row can remain listed after its window and health evidence have been withdrawn. AIR evaluates
readiness when it handles an inference request, which can wait for a model or use a different
engine or profile.

### HY4 Flash on the shared Maya listener

Use the existing Maya engine catalog, shown as `engine.glm` in
[samples/router.toml](../samples/router.toml). The operator selects GLM, HY4 Flash
or MiniMax M3.1 Flash for this listener. Supply the deployment’s authorized upstream
credential if required.
Ask for `hy4-flash`, `hy4` or `hy4-flash-maya-xs` when Maya reports the canonical
`hy4-flash` row as loaded.

The catalog defines seven context targets from 32k to 1024k. AIR's omitted launch
tier resolves to 96k, a 98,304-token target sized for 65,536 input plus 32,768 output
tokens. Choose 32k explicitly for a smaller launch. The installed selector still
accepts and defaults to 32k. AIR's 96k default requires selector support and host
qualification before it can launch. AIR forwards against the measured window for
the loaded model. The operator retains control of which model and tier Maya loads.

### MiniMax M3.1 Flash on the shared Maya listener

Use the same external `engine.glm` listener. Ask AIR for `minimax-m3.1-flash`,
`minimax` or `minimax-maya-xs` when Maya reports the canonical `minimax-m3.1-flash`
row loaded. The sample declares one slot per tier at 32k, 64k, 96k, 128k, 256k,
512k and 1024k, using 1,024 tokens per k. Choose 32k explicitly as the safe fallback.
Normal choices start at 64k. The omitted launch tier is **nominal 96k** (98,304
tokens). The operator controls the external launch. AIR forwards against the
model-bound window it observes, so a loaded 32k model may serve smaller requests
and refuses a 65,536-token floor on this listener.

The archived MiniMax selector projection covers 32k only. Wider launches require
selector support and host qualification. With the pinned Maya eight-token reserve,
nominal 96k leaves 98,296 usable tokens and 32,760 output tokens after a 65,536-token
rendered prompt. The full 65,536 plus 32,768 split needs at least 98,312 nominal
tokens on that path and separate workload qualification. [Historical launch projection](selector-launch-coverage.md#minimax-32k-launch-options) · [Maya context envelope](hardware-tuning-design.md#qualify-the-normal-use-context-budget)

## Thread identity: how an agent gets its share

Send a stable id on every request:

```
X-Inference-Thread: claude-session-7
```

An omitted header gives each request its own scheduling identity. Explicit names share the named
thread's cap. This includes legacy clients that send `anon`. Omit the header to migrate those clients
to request-scoped admission. Register a named thread to set its priority and affinity.

```bash
curl -X POST http://127.0.0.1:8321/admin/threads \
  -H 'content-type: application/json' \
  -d '{"threadId":"claude-session-7","priority":{"class":"interactive"}}'
```

Classes are `background`, `low`, `normal`, `high`, `interactive`, each with an optional `sub` of
0-255 as a tie-break. Unregistered threads and unlabelled requests run at the config default.

**Honest caveat:** the router does not yet clamp what a caller asks for. `X-Inference-Priority` and
`POST /admin/threads` accept any class, and the admin surface has no authentication, so today this
is a courtesy system, not an enforced one — keep the router on loopback or behind your own proxy
until v0.1 ingress work lands (see the note on the hardening task in the board).

The queue does bound waiting: a ticket waits at most `queue.max_age_seconds` past its share, and the
highest-priority waiter is never blocked by more than `queue.priority_window` others.

## Chat

```bash
curl -N http://127.0.0.1:8321/v1/chat/completions \
  -H 'content-type: application/json' \
  -H 'X-Inference-Thread: claude-session-7' \
  -d '{"model":"qwen-coder","stream":true,"messages":[{"role":"user","content":"hi"}]}'
```

Streaming is the default. AIR forwards complete upstream SSE blocks verbatim.

- **Keepalives until the first complete block.** AIR sends an initial SSE comment and one every four
  seconds until it has a complete upstream SSE block or the request ends. An upstream comment-only
  block counts. A keepalive looks like `: [router keepalive seq=7 status="switching model=qwen-long engine=hyperqwen"]`.
  SSE parsers skip comments when dispatching data events. The status appears as
  a JSON string on the first comment and when it changes. In text mode, `air chat` shows these
  status changes on stderr.
- **An explicit end on failure.** AIR preserves complete blocks and discards an unfinished block.
  EOF before the blank delimiter and blocks exceeding 1 MiB before that delimiter both produce
  `engine_error`. A `[DONE]` line without its blank delimiter is unfinished. A read fault
  produces `engine_unreachable`. The failure ends with `data: {"error":{"type":"air_error","code":…,"message":…}}`
  followed by `data: [DONE]`.

Send `X-Inference-Required-Context: 24000` to give the scheduler a context requirement in
tokens for choosing a profile or requesting a switch. Without the header, AIR estimates
demand from prompt characters. When the built-in scheduler selects a required profile, a loaded
model needs a matching observed window to serve it. If the window is unknown, the request waits
for capacity evidence. A request with no required profile can still use a healthy loaded model.
Fit checks through dispatch and switch restoration are still in progress.
[Serving gauge verification](../backlog/tasks/task-68%20-%20Bind-the-serving-gauges-to-a-capability-epoch-so-a-stale-window-cannot-authorise-a-profile-the-engine-cannot-serve.md)

## What to call when something goes wrong

| Need | Call |
| --- | --- |
| The id of the request you just made | response header `X-Inference-Ticket` (streaming and non-streaming) |
| Which model IDs AIR lists for each engine and what windows it observed | `GET /v1/models` |
| Who is running, queue depths, per-thread state | `GET /admin/status` |
| What the router decided, replayably | `GET /admin/events?since=<seq>` (SSE); filters `kinds=a,b`, `thread=`, `engine=`, `ticket=` |
| Only the events for the request you own | `GET /admin/tickets/{id}/events?since=<seq>` (SSE) |
| One request's state | `GET /admin/tickets/{id}` |
| Cancel a queued or running request | `DELETE /admin/tickets/{id}` |
| Load a model on a running engine | `POST /admin/switch {"engine":…,"model":…}` |
| Check an explicit model switch | `GET /admin/switch/{operationId}`, using the `OperationId` from the 202 acknowledgement |

Save the operation ID and poll the same running router. Branch on `Status` and the structured
`Reason`. A terminal result remains queryable for 15 minutes from the later of settlement and
the server's acknowledgement completing or its send terminating. Retry the POST later if it
returns `503 too_many_switch_operations`.

### Headers the router reads

| Header | Effect |
| --- | --- |
| `X-Inference-Thread` | Optional named conversation identity for priority and queueing |
| `X-Inference-Required-Context` | Requested context in tokens for profile choice and switching |
| `X-Inference-Priority` | per-request priority override (not clamped today — see the caveat above) |
| `X-Inference-Affinity` | opaque hash the scheduler uses to keep a request on an engine that already holds its prefix |

The engine's own API key never comes from the client: the router holds it (config `api_key_file`)
and injects it on the upstream call.

Error bodies are `{"error":{"type":…,"code":…,"message":…}}`, and `code` decides the status:
`invalid_request` 400, `not_found` 404, `deadline_exceeded` 408, `queue_overflow` 429, `cancelled`
499, `engine_error` / `engine_unreachable` 502, `engine_timeout` 504, `switch_failed` /
`model_unavailable` 503, `no_available_engine` 500. Retrying is only safe when no token has been
emitted yet: once the first token is out the response is committed, and a retry would duplicate
output.

The `message` in that body is a bounded excerpt — at most 1600 characters, cut with a marker that names how much is missing —
however long the upstream sentence or the value it quotes happens to be. Branch on `code`, not on the wording.

A `switch_failed` message names the model it could not serve and, when the caller's own switch attempt
**ended**, ends with one reason name in parentheses: `no_select_command`, `launch_failed`, `health_timeout`,
`refresh_omits`, `refresh_unanswered`, `window_too_small`, `switch_error`, `no_lifecycle_owner`. The name is
the whole reason — it never arrives with a model name or a millisecond count attached to it — so match the
name instead of reading the sentence around it. Three of them are configuration mistakes that retrying cannot
fix: `no_select_command` (nothing in the configuration can load that model on that engine), `refresh_omits`
(the engine answers, and never names the model — the catalog key does not match what the engine reports) and
`window_too_small` (the model is served, but in a context window smaller than the profile you asked for: a
different profile, or a bigger tier on that engine). Three mean the engine did not answer in time and are
usually worth a second attempt later: `launch_failed`, `health_timeout`, `refresh_unanswered`. `switch_error`
is the catch-all: retry. `no_lifecycle_owner` means the router cannot load models into that engine at all, so
aim at one it does own — retrying the same engine repeats the refusal. A message with **no** name at all means
your wait ran out while a switch was still running, and the model may well be loaded by the time you ask
again. The operator's full explanation — what the engine actually reported, what the selector said, how long
the readiness window was — is on the event feed, not in your response.

Two of those codes mean the router gave up on your behalf instead of waiting forever. If an engine
takes the request and then says nothing past its `response_timeout_ms`, the request ends
`engine_timeout` and the engine's slot is handed back — retrying is reasonable there, and aiming at a
different model usually beats retrying the same one. On a streaming request the failure looks different
once the answer has started: the `200` is already committed, so there is no status to change and the
error arrives inside the stream as one `engine_timeout` frame followed by `[DONE]`.
The ceiling ends at the engine's first positive body read. AIR can continue sending keepalive comments
until the first complete upstream SSE block.
That gives one reliable rule: a 504 means nothing was emitted, so retrying cannot duplicate output; an
in-band error frame means part of an answer may already have reached you. If a request is parked in `WaitingForModel` while
the router loads a bigger model, `switch_wait_timeout_ms` decides how long the queue will wait for that
load before the request fails `switch_failed`; the load continues either way, so retrying the same
request shortly after usually lands. Both are deployment config, so ask the operator what the ceilings
are rather than assuming the defaults.

## Worked example: a session that needs a bigger context

1. The agent chats normally on `qwen-coder` with `max_tokens` near 4k.
2. A long pasted log needs more context than the loaded model's window. The scheduler says
   "switch": the ticket parks in `WaitingForModel` instead of failing, and the client sees keepalive
   frames with `status="switching model=qwen-long engine=hyperqwen"`.
3. The router drains that engine, loads `qwen-long`, waits for the health probe, and records
   `engine.switch_started` then `engine.switch_completed`. Requests already in flight finished first;
   nobody got a truncated response. If no enabled profile fits, the request fails with
   `no_available_engine` rather than being silently clipped.
4. The agent retries **the same request** — same URL, same model name — and it now fits.

Nothing in that sequence needs agent-side knowledge of engines, GPUs or file paths. If you want
to watch it happen while developing, `air events` tails the same ring the admin API serves.

## Tool manifest

[`samples/pi/inference-router-tools.json`](../samples/pi/inference-router-tools.json) is a JSON
description of the calls above in the shape tool-calling harnesses expect (name, description,
JSON-Schema parameters). Point a harness at it, or transcribe the pieces you need — the endpoints
are ordinary HTTP and nothing in the router requires this file.
