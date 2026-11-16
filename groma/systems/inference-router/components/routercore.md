---
type: C4 Component
title: RouterCore
status: stable
groma:
  id: routercore
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/RouterCore.cs
  group: request-routing
description: 'Request coordinator: admission, routing, queues, engine registry.'
---

Resolves the model route and thread identity for a call, admits or rejects against per-model queue caps and engine slots, hands tickets to the per-model queue, asks the scheduler which engine should take the next ticket, and drives the exchange through EngineRuntime. It also serialises model switches (drain, lifecycle command, resume), registers threads and changes their priority, and appends to the event ring that /admin/events tails.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Queue/ModelQueue.cs](../../../../src/Arboreal.InferenceRouter/Queue/ModelQueue.cs) | Queues tickets | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs](../../../../src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs) | Selects engines | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | Dispatches to engines | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Events/RouterEvent.cs](../../../../src/Arboreal.InferenceRouter/Events/RouterEvent.cs) | Publishes events | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Threading/RouterThread.cs](../../../../src/Arboreal.InferenceRouter/Threading/RouterThread.cs) | Tracks conversations | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfig.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfig.cs) | Reads startup config | in-process call |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs) | Config built by this loader | startup composition |
| [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | [model-loader](../../../externals/model-loader.md) | Requests a model load | via the engine runtime |
