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
