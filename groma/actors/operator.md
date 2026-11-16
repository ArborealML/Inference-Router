---
type: C4 Actor
title: Operator
status: stable
groma:
  id: operator
---

Writes the TOML, starts `air serve`, watches /admin/status and /admin/events, and owns the scripts that load models into engines.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [operator](operator.md) | [air-cli](../systems/inference-router/containers/air-cli/container.md) | Runs and configures | command line |
| [operator](operator.md) | [air-server](../systems/inference-router/containers/air-server/container.md) | Watches and switches | HTTP admin API |
