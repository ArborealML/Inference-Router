---
type: C4 Component
title: ThreadPriority
status: stable
groma:
  id: threadpriority
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Priority/ThreadPriority.cs
  group: identity-and-priority
description: Priority class used for queue ordering.
---

The priority model consumed by queue ordering and the scheduler; changing a thread's class re-sorts queued tickets in place.
