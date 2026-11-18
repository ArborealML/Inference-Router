---
type: C4 Component
title: Host reservation record
status: stable
groma:
  id: hostreservation
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/HostReservation.cs
description: Serializes cooperating launchers through an exclusive file handle and records unresolved stop or launch operations across restarts.
---

Each grant advances the epoch in a pre-provisioned, checksummed file. The coordinator records an operation before dispatch. A subsequent holder must reconcile a pending operation before it can obtain a new grant. The provider supplies evidence about physical occupancy.
