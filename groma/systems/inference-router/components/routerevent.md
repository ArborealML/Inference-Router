---
type: C4 Component
title: RouterEvent and EventRing
status: stable
groma:
  id: routerevent
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Events/RouterEvent.cs
description: Event record plus the bounded in-memory ring.
---

RouterEvent is the wire shape for /admin/events (kind, model, engine, thread, ticket, message). EventRing is the bounded buffer the admin stream tails, so a slow or absent consumer cannot grow memory.
