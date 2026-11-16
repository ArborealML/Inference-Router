---
type: C4 Component
title: CLI entry point
status: stable
groma:
  id: arboreal-inferencerouter-cli-program
  parent: air-cli
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter.Cli/Program.cs
      symbol: Program
description: 'Verb dispatch: serve, chat, complete, models, status, events, config.'
---

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter.Cli/Program.cs](../../../../../../src/Arboreal.InferenceRouter.Cli/Program.cs) | [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | Hosts, and drives over HTTP | in-process call; HTTP |
| [src/Arboreal.InferenceRouter.Cli/Program.cs](../../../../../../src/Arboreal.InferenceRouter.Cli/Program.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs](../../../../../../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs) | Validates config files | file read |
