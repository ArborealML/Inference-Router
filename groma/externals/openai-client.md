---
type: C4 System
title: OpenAI-compatible client
status: stable
groma:
  id: openai-client
  technology: OpenAI-compatible HTTP client
---

Any caller that speaks the OpenAI wire format: agent harnesses, SDK clients, scripts. It sees one endpoint and one model list, not the engines behind them.

## Relationships

| Source | Target | Description | Technology |
| --- | --- | --- | --- |
| [openai-client](openai-client.md) | [src/Arboreal.InferenceRouter.Server/ServerApp.cs](../../src/Arboreal.InferenceRouter.Server/ServerApp.cs) | Requests completions | OpenAI-compatible HTTP |
