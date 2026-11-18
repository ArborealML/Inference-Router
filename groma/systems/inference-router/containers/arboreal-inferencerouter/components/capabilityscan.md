---
type: C4 Component
title: CapabilityScan
status: stable
groma:
  id: capabilityscan
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Scheduling/CapabilityScan.cs
description: Decides, from one snapshot per engine, whether a model is hopeless here — and refuses only on certainty.
---

Answers the one question admission is allowed to refuse on: could no engine in this configuration ever take this model? The rule is deliberately one-way — an engine that has never answered the model probe, a switch still in flight, or a capability snapshot that moved mid-scan all answer "not hopeless", and the request waits. Each engine contributes one immutable `EngineCapability` (version, health, reported models, switch target), so a refusal cannot be spliced out of readings taken at different instants; a scan that catches a publication retries once, and a second unstable scan is doubt. A caller acting on `true` must still claim the ticket before failing it: this is a statement about capability, not about queue ownership.
