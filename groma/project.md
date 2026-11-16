---
type: Groma Project
title: Arboreal Inference Router
groma:
  profile: architecture
description: A Native-AOT inference router that puts one OpenAI-compatible front door in front of several local engines.
---

Library, server and `air` command line. Routing decisions combine per-model queues, thread priority, serving-profile capacity and prompt-prefix affinity so a conversation keeps hitting the engine whose KV cache already holds its context. Engines are declared in TOML; model loading happens through operator lifecycle commands.
