---
type: C4 Component
title: Server entry point
status: stable
groma:
  id: arboreal-inferencerouter-server-program
  parent: air-server
  code:
    - scanner: csharp
      file: src/Arboreal.InferenceRouter.Server/Program.cs
description: Starts the listener from a config path.
---

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [src/Arboreal.InferenceRouter.Server/Program.cs](../../../../../../src/Arboreal.InferenceRouter.Server/Program.cs) | [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../../../../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | Starts the listener | in-process call |
| [src/Arboreal.InferenceRouter.Server/Program.cs](../../../../../../src/Arboreal.InferenceRouter.Server/Program.cs) | [src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs](../../../../../../src/Arboreal.InferenceRouter/Config/RouterConfigLoader.cs) | Loads the config | file read |
