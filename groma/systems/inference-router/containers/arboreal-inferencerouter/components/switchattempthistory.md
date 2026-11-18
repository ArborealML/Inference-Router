---
type: C4 Component
title: Model switch timing and cost estimates
status: stable
groma:
  id: switchattempthistory
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Scheduling/SwitchAttemptHistory.cs
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Scheduling/SwitchAttempt.cs
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Scheduling/SwitchCostOrder.cs
description: Records timed switch stages and outcomes, retains comparable samples, and ranks eligible candidates by age and measured cost. Production dispatch integration is in progress.
---
