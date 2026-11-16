---
type: C4 Component
title: PrefixTrie
status: stable
groma:
  id: prefixtrie
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Prefix/PrefixTrie.cs
  group: request-routing
description: Prompt-prefix index behind cache affinity.
---

Maps shared prompt prefixes to the engine that served them, so later turns of a conversation land where the KV cache already holds the context.
