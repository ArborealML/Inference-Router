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

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs) | [src/Arboreal.InferenceRouter/Config/ConfigDto.cs](../../../../src/Arboreal.InferenceRouter/Config/ConfigDto.cs) | Deserializes TOML | source-generated TOML binding |
| [src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfig.cs](../../../../src/Arboreal.InferenceRouter/Config/RouterConfig.cs) | Builds startup config | in-process call |
