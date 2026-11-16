---
type: Groma Flow
title: Route a chat completion
groma:
  id: route-a-chat-completion
---

A client request is admitted, queued per model, placed on the engine most likely to hold its prompt prefix, and relayed back token by token.

## Steps

| From | To | Action |
| --- | --- | --- |
| [OpenAI-compatible client](../externals/openai-client.md) | [ServerApp](../systems/inference-router/containers/air-server/components/serverapp.md) | POST /v1/chat/completions |
| [ServerApp](../systems/inference-router/containers/air-server/components/serverapp.md) | [RouterCore](../systems/inference-router/components/routercore.md) | Resolves the route and admits the ticket |
| [RouterCore](../systems/inference-router/components/routercore.md) | [ModelQueue](../systems/inference-router/components/modelqueue.md) | Enqueues behind the model's queue |
| [RouterCore](../systems/inference-router/components/routercore.md) | [PrefixAwareScheduler](../systems/inference-router/components/prefixawarescheduler.md) | Asks which engine should take it |
| [RouterCore](../systems/inference-router/components/routercore.md) | [EngineRuntime](../systems/inference-router/components/engineruntime.md) | Dispatches and relays the stream |
| [EngineRuntime](../systems/inference-router/components/engineruntime.md) | [Inference engine](../externals/inference-engine.md) | Forwards the body byte for byte |
