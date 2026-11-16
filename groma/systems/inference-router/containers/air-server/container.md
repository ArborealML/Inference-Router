---
type: C4 Container
title: Router HTTP server
status: stable
groma:
  id: air-server
  parent: inference-router
  technology: .NET Minimal APIs, Native AOT
description: Minimal-API host for the OpenAI-compatible and admin surfaces.
---

Routes: POST /v1/chat/completions, POST /v1/completions, GET /v1/models; GET /admin/status, GET /admin/events (SSE), POST /admin/threads, POST /admin/switch, GET and DELETE /admin/tickets/{id}; GET /healthz. Admin JSON is PascalCase and pinned by tests. The event stream relays frames with keepalives so idle connections survive. `air serve` hosts the same surface in-process.
