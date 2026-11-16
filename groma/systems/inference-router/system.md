---
type: C4 System
title: Inference Router
status: stable
groma:
  id: inference-router
description: One OpenAI-compatible front door in front of several local inference engines.
---

The router accepts OpenAI-compatible requests, keeps one queue per model, picks the engine most likely to reuse its prompt cache (prefix affinity within a serving profile's capacity), relays the answer including streamed token deltas, and exposes an admin surface for state, tickets, events and model switches. It owns no weights: engines are declared in TOML and model loading happens through operator lifecycle commands. Everything ships Native AOT, so JSON and TOML go through source-generated contexts and the request path stays allocation-light. The library is embedded in both executables: the `air` command line and the standalone server.
