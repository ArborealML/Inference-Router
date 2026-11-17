---
type: C4 Component
title: DockerLifecycleProvider
status: stable
groma:
  id: dockerlifecycleprovider
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/DockerLifecycleProvider.cs
      symbol: DockerLifecycleProvider
description: 'The native lifecycle provider: the router drives the Docker Engine API itself.'
---

Raw Engine HTTP over the unix socket or TCP with a pinned API version and per-call deadlines. Containers it starts carry `arboreal.router`, `arboreal.engine` and `arboreal.model`; the daemon's label filter is treated as an optimisation and labels are re-checked locally, so a foreign container is never ours to stop. The image must already be on the host — it never pulls — and a daemon that is unreachable, refuses the router, or goes quiet produces one actionable message and no attempt to escalate.
