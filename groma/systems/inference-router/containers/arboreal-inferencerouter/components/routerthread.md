---
type: C4 Component
title: RouterThread
status: stable
groma:
  id: routerthread
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Threading/RouterThread.cs
  group: identity-and-priority
description: Conversation identity and its routing hints.
---

Ties a series of requests to a priority class, weight and optional model affinity so follow-ups keep their queue position and prefix affinity.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter/Threading/RouterThread.cs](../../../../../../src/Arboreal.InferenceRouter/Threading/RouterThread.cs) | [src/Arboreal.InferenceRouter/Priority/ThreadPriority.cs](../../../../../../src/Arboreal.InferenceRouter/Priority/ThreadPriority.cs) | Carries priority class | in-process call |
