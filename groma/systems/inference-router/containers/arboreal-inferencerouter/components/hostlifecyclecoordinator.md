---
type: C4 Component
title: Host lifecycle coordinator
status: stable
groma:
  id: hostlifecyclecoordinator
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/HostLifecycleCoordinator.cs
description: Orders host observation, target checks, admission drain and engine replacement for cooperating launchers.
---

The coordinator reconciles any pending operation before granting a new reservation. It rechecks ownership before stopping an incumbent and requires fresh evidence of vacancy before launch. When a runtime requests publication, the provider verifies the launched instance before the coordinator commits its route under the admission guard.
