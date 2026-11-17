---
type: C4 Component
title: ConfigDto
status: stable
groma:
  id: configdto
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Config/ConfigDto.cs
  group: configuration
description: DTO graph for the source-generated TOML reader.
---

Required by Native AOT: every config key is a declared DTO property, so no reflection-based deserialisation happens at startup.
