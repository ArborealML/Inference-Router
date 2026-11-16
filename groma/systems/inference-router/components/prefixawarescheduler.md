---
type: C4 Component
title: PrefixAwareScheduler
status: stable
groma:
  id: prefixawarescheduler
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs
  group: request-routing
description: Chooses which engine takes the next ticket.
---

Takes a SchedulerView (engine health, load, loaded models) plus the startup config and returns the engine for a ticket, preferring an engine that already holds the prompt prefix and has capacity under the ticket's serving profile.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs](../../../../src/Arboreal.InferenceRouter/Scheduling/PrefixAwareScheduler.cs) | [src/Arboreal.InferenceRouter/Prefix/PrefixTrie.cs](../../../../src/Arboreal.InferenceRouter/Prefix/PrefixTrie.cs) | Ranks by matched prefix | value from the engine runtime's trie |
