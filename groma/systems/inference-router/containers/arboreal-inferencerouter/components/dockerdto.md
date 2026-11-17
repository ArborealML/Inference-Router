---
type: C4 Component
title: DockerDto
status: stable
groma:
  id: dockerdto
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/DockerDto.cs
description: The Docker Engine request and response shapes this provider uses.
---

Explicit DTOs with pinned property names, serialized only through a source-generated context: no reflection, so the Native AOT publish stays trim-clean and a daemon update cannot silently change the casing the code depends on.
