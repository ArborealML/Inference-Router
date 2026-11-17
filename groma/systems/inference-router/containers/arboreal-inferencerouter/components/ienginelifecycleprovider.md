---
type: C4 Component
title: IEngineLifecycleProvider
status: stable
groma:
  id: ienginelifecycleprovider
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/IEngineLifecycleProvider.cs
description: The seam between the router and whatever can load a model on an engine.
---

One interface with four questions in a fixed order: what exists that we own, may this switch proceed, stop these units, start this exact target. Providers never decide the sequence, which is what lets a delegated selector program, the Docker Engine API, or a test fake drive the same switch path.
