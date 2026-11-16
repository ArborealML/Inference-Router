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
