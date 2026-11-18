---
type: C4 Component
title: Engine runtime host enrollment
status: stable
groma:
  id: engineruntime-hostlifecycle
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/EngineRuntime.HostLifecycle.cs
description: Publishes a verified model to the engine runtime while the enrolled host controller holds admission closed.
---

An internally enrolled engine passes its switch target and publication callbacks to the host controller. On success the runtime records the model, emits the completion event and clears the switch marker. Tests supply the enrollment.
