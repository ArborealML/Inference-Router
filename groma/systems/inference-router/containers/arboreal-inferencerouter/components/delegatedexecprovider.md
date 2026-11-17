---
type: C4 Component
title: DelegatedExecProvider
status: stable
groma:
  id: delegatedexecprovider
  parent: arboreal-inferencerouter
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter/Engines/Lifecycle/DelegatedExecProvider.cs
      symbol: DelegatedExecProvider
description: The lifecycle provider behind `select = { exec = … }`.
---

Runs the operator's selector program, waits for it with a timeout that kills the process tree, drains both pipes so a chatty loader cannot deadlock, and treats a non-zero exit as a failure with the program's stderr tail. It claims no containers, so the router never stops anything on a delegated engine.
