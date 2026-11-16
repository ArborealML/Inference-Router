---
type: C4 Component
title: Program
status: stable
groma:
  id: arboreal-inferencerouter-cli-program
  parent: arboreal-inferencerouter-cli
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter.Cli/Program.cs
      symbol: Program
---

## Derived relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter.Cli/Program.cs](../../../../../../src/Arboreal.InferenceRouter.Cli/Program.cs) | [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | Calls HTTP endpoints: POST /admin/switch, POST /admin/threads, POST /v1/chat/completions | csharp |
