---
type: C4 Component
title: Program
status: stable
groma:
  id: arboreal-inferencerouter-benchmarks-program
  parent: arboreal-inferencerouter-benchmarks
  code:
    - scanner: csharp
      file: bench/Arboreal.InferenceRouter.Benchmarks/Program.cs
---

## Derived relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [bench/Arboreal.InferenceRouter.Benchmarks/Program.cs](../../../../../../bench/Arboreal.InferenceRouter.Benchmarks/Program.cs) | [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | Calls HTTP endpoint: POST /v1/chat/completions | csharp |
