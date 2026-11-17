# Calling the router from an agent

The router is a plain HTTP server with an OpenAI-compatible request path, so any agent that can
POST JSON can use it. What it adds over pointing an agent straight at one engine:

- **A URL that survives a model switch.** When the router loads a different model on the same
  engine, your base URL and model list keep working; you do not have to know which engine is live.
- **A call that cannot over-run its budget.** A thread gets its configured share of the engine's
  context window and the queue gets bounded, so a runaway agent is rejected with `429` instead of
  pushing the engine into a context overflow.
- **One place to watch what happened.** Every decision lands on a bounded event ring: `Seq`,
  `Timestamp`, `Kind`, and whichever of `ThreadId`, `TicketId`, `Engine`, `Model`, `Reason`, `Value`
  apply (admin JSON is PascalCase, and `EventSurfaceTests` pins it). `Seq` is monotonic, so a gap in
  the numbers you receive means events were dropped — the ring keeps the last 4096, and a slow SSE
  subscriber's buffer is bounded rather than allowed to grow. Kinds you will see on a healthy box: `request.enqueued`, `request.selected`,
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

## Thread identity: how an agent gets its share

Send a stable id on every request:

```
X-Inference-Thread: claude-session-7
```

Absent header means the anonymous thread, which always runs at the configured default priority.
Registered threads get a priority class capped by the router's default, so an agent can be
deprioritised but cannot escalate itself:

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

Streaming is the default, and frames from the engine are passed through verbatim. Two things the
router adds:

- **Keepalives while it is not your turn.** While a request is queued or a model is loading, the
  router emits `data: [router keepalive seq=7 status="switching model=qwen-long engine=hyperqwen"]`
  every four seconds. Parse-and-ignore those frames; they exist so client idle timeouts do not fire
  during a queue wait or a switch.
- **A clean end on failure.** If the stream fails after it started, the last frames are
  `data: {"error":{"code":…, "message":…}}` then `data: [DONE]`. A client that stops at `[DONE]`
  will not hang, and can tell a truncated completion from a finished one.

Declare a context need instead of guessing the window: send `X-Inference-Required-Context: 24000`
(tokens) and the router will move the request to a profile/engine that can serve it rather than
cramming it into the active window. Without the header it estimates the demand from prompt
characters.

## What to call when something goes wrong

| Need | Call |
| --- | --- |
| The id of the request you just made | response header `X-Inference-Ticket` (streaming and non-streaming) |
| Which models exist right now, and which engine owns each | `GET /v1/models` |
| Who is running, queue depths, per-thread state | `GET /admin/status` |
| What the router decided, replayably | `GET /admin/events?since=<seq>` (SSE); filters `kinds=a,b`, `thread=`, `engine=`, `ticket=` |
| Only the events for the request you own | `GET /admin/tickets/{id}/events?since=<seq>` (SSE) |
| One request's state | `GET /admin/tickets/{id}` |
| Cancel a queued or running request | `DELETE /admin/tickets/{id}` |
| Load a model on a running engine | `POST /admin/switch {"engine":…,"model":…}` |

### Headers the router reads

| Header | Effect |
| --- | --- |
| `X-Inference-Thread` | conversation identity: its priority class, context budget and queue share |
| `X-Inference-Required-Context` | tokens you need served; drives profile choice and switching |
| `X-Inference-Priority` | per-request priority override (not clamped today — see the caveat above) |
| `X-Inference-Affinity` | opaque hash the scheduler uses to keep a request on an engine that already holds its prefix |

The engine's own API key never comes from the client: the router holds it (config `api_key_file`)
and injects it on the upstream call.

Error bodies are `{"error":{"type":…,"code":…,"message":…}}`, and `code` decides the status:
`invalid_request` 400, `not_found` 404, `deadline_exceeded` 408, `queue_overflow` 429, `cancelled`
499, `engine_error` / `engine_unreachable` 502, `switch_failed` / `model_unavailable` 503,
`no_available_engine` 500. Retrying is only safe when no token has been emitted yet: once the first
token is out the response is committed, and a retry would duplicate output.

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
