---
id: decision-61
title: Send stream progress in SSE comments and preserve upstream completion data
date: '2026-11-18T18:42:00Z'
status: accepted
---
## Context

A strict OpenAI completion reader attempted to decode the router's queued progress as JSON and failed before the first upstream chunk. TASK-108 reproduced the failure through the server's normal streaming route.

## Decision

Send router-generated keepalives as single-line SSE comments. Each comment carries the per-stream sequence and may include a JSON-quoted status when it changes. Preserve upstream completion bytes and their sentinel. The bundled CLI displays status changes on stderr in human mode and suppresses comments in JSON mode.

## Consequences

Clients that previously consumed router progress as data events must read the reserved comment format to keep displaying progress. Conforming OpenAI readers can ignore comments. TASK-81 tracks ownership of terminal output when an upstream fails after sending its sentinel.
