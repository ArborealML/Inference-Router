---
type: C4 Component
title: RouterConfig
status: stable
groma:
  id: routerconfig
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Config/RouterConfig.cs
  group: configuration
description: Immutable startup configuration.
---

Typed config compiled once at start: engine definitions, capabilities, serving profiles, probes, lifecycle commands and model catalogues, plus scheduler limits, threads and defaults. Nothing re-reads TOML at runtime, which is what keeps the hot path allocation-light.
