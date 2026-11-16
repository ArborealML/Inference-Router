---
type: C4 Component
title: RouterConfigLoader
status: stable
groma:
  id: routerconfigloader
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs
      symbol: RouterConfigLoader
  group: configuration
description: TOML to RouterConfig, with validation.
---

Reads a config file (or ARBOREAL_ROUTER_CONFIG), deserialises it into the DTO graph, validates and compiles it into RouterConfig, and reports failures with the offending key. `air config` runs this without binding sockets or executing lifecycle commands.
