---
id: TASK-16
title: Survey which inference APIs the router should front
status: To Do
assignee: []
created_date: '2026-11-16T23:02:00Z'
labels: []
dependencies: []
priority: low
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner ask (2026-11-16): find out which upstream inference APIs the router should speak besides OpenAI-compatible. Anthropic's Messages API is the obvious one. The open question is the local engine world: does Strata expose a native API (it already has /props and /models probes beyond OpenAI shape), and does Project Maya? Also worth pricing in: Ollama, llama.cpp's llama-server, vLLM/SGLang, LM Studio, and OpenAI's newer Responses API (versus chat/completions). Deliverable is a decision with consequences, not a survey dump: which 1-2 adapters to build, which to decline, and what the engine-kind seam in EngineRuntime/RouterConfig needs to support them. Sequence AFTER the README updates (TASK-14). The research request goes to the research service (deep tier) once the research service is back up from its update; keep the idempotency key stable across reconciliations.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Request submitted to the research service (deep tier) after TASK-14 lands, and its answer mirrored into this task body
- [ ] #2 Candidate APIs compared on what they cost us: streaming shape, tool/thinking blocks, cancellation, context-window signalling, auth, AOT-safe JSON story
- [ ] #3 A decision recorded in SPEC: which adapters are in for v0.2, which are explicitly out, and the engine 'kind' taxonomy that supports both
- [ ] #4 Any new kind gets a fake-handler wire test before real engine wiring
<!-- AC:END -->
