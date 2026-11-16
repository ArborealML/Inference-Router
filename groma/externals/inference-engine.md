---
type: C4 System
title: Inference engine
status: stable
groma:
  id: inference-engine
  technology: Local OpenAI-compatible engine (llama.cpp-style server)
---

The upstream engine the router fronts: a state/props probe, a model catalogue, and chat/completions. The router never loads weights itself.
