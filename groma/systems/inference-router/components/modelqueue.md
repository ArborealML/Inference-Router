---
type: C4 Component
title: ModelQueue
status: stable
groma:
  id: modelqueue
  parent: inference-router
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Queue/ModelQueue.cs
  group: request-routing
description: One bounded queue per model, ordered by thread priority then arrival.
---

Ordering is thread priority class first, then enqueue sequence, re-sorted in place when a thread's priority changes while queued. A full queue produces the router's 429 shape on the wire; a cancelled ticket leaves without consuming a slot.
