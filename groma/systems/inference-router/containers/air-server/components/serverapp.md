---
type: C4 Component
title: ServerApp
status: stable
groma:
  id: serverapp
  parent: air-server
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter.Server/ServerApp.cs
      symbol: ServerApp
description: HTTP handlers for /v1 and /admin.
---

Maps the routes, turns RouterCore outcomes into OpenAI-shaped JSON or SSE, and maps router failures onto statuses (no usable upstream becomes 502 engine_error). Deliberately thin: routing and admission decisions stay in the library.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | [src/Arboreal.InferenceRouter/RouterCore.cs](../../../../../../src/Arboreal.InferenceRouter/RouterCore.cs) | Routes inference requests | in-process call |
| [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | [src/Arboreal.InferenceRouter/Events/RouterEvent.cs](../../../../../../src/Arboreal.InferenceRouter/Events/RouterEvent.cs) | Streams events | SSE frames |
| [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | [src/Arboreal.InferenceRouter.Server/AirServerJson.cs](../../../../../../src/Arboreal.InferenceRouter.Server/AirServerJson.cs) | Encodes payloads | source-generated JSON |
