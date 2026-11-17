---
type: C4 Container
title: Inference Router library
description: 'The .NET library both executables embed: the routing core, queues, scheduling, engine clients and lifecycle providers.'
status: draft
groma:
  id: arboreal-inferencerouter
  parent: inference-router
---

Holds everything the router *does*: it admits a request, picks an engine by thread identity, priority and prefix affinity within a serving profile's capacity, queues per model, calls the upstream engine and relays the answer including streamed deltas, and records every decision on the event feed. It also owns the lifecycle seam that loads a model — the delegated selector program or the Docker Engine API — and the immutable config parsed once at startup. No HTTP listener of its own: it is compiled into both `air` and the standalone server, and ships Native AOT with source-generated JSON and TOML so the request path stays allocation-light.
