---
type: C4 Component
title: EngineRuntime
status: stable
groma:
  id: engineruntime
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  group: engine-access
description: 'One engine: health, capacity, loaded set, request relay.'
---

Owns the HTTP endpoint for an engine: state probe, model catalogue at the configured `models_endpoint`, loaded-model set, live slot accounting, byte-for-byte forwarding of chat/completion bodies, and response relay including SSE. `remote = true` engines are probed and routed but never lifecycle-managed; local engines may additionally carry lifecycle commands.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [src/Arboreal.InferenceRouter/Prefix/PrefixTrie.cs](../../../../src/Arboreal.InferenceRouter/Prefix/PrefixTrie.cs) | Indexes prompt prefixes | in-process call |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfig.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfig.cs) | Uses engine definitions | in-process call |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [src/Arboreal.InferenceRouter/Events/RouterEvent.cs](../../../../src/Arboreal.InferenceRouter/Events/RouterEvent.cs) | Records engine state | in-process call |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [inference-engine](../../../externals/inference-engine.md) | Runs completions | HTTP |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [src/Arboreal.InferenceRouter/Priority/ThreadPriority.cs](../../../../src/Arboreal.InferenceRouter/Priority/ThreadPriority.cs) | Priority applied upstream | no direct use; queued ticket order |
| [src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs](../../../../src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs) | [model-loader](../../../externals/model-loader.md) | Runs the operator's loader | shell command |
