---
type: Groma Flow
title: Switch a model on a running engine
groma:
  id: switch-a-model-on-a-running-engine
---

An operator asks for a different model on the same engine: admitted work drains, the operator's loader runs, and readiness is proved before admission reopens.

## Steps

| From | To | Action |
| --- | --- | --- |
| [Operator](../actors/operator.md) | [Router HTTP server](../systems/inference-router/containers/air-server/container.md) | POST /admin/switch |
| [ServerApp](../systems/inference-router/containers/air-server/components/serverapp.md) | [RouterCore](../systems/inference-router/containers/arboreal-inferencerouter/components/routercore.md) | Serialises the switch |
| [RouterCore](../systems/inference-router/containers/arboreal-inferencerouter/components/routercore.md) | [EngineRuntime](../systems/inference-router/containers/arboreal-inferencerouter/components/engineruntime.md) | Drains the engine, then asks for the target |
| [EngineRuntime](../systems/inference-router/containers/arboreal-inferencerouter/components/engineruntime.md) | [Model loader](../externals/model-loader.md) | Runs the lifecycle command and proves readiness |
