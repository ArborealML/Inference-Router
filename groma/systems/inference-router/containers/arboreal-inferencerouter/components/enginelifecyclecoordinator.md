---
type: C4 Component
title: EngineLifecycleCoordinator
status: stable
groma:
  id: enginelifecyclecoordinator
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/EngineLifecycleCoordinator.cs
      symbol: EngineLifecycleCoordinator
description: Runs one lifecycle transaction in the order that makes it safe.
---

Proves ownership, preflights, stops only what the ownership proof claims, then launches the exact target; one transaction per engine at a time, and a failed ownership query refuses the whole switch rather than reading as "nothing is running". It keeps a short journal so an operator can see why a switch was refused.
