---
id: TASK-16
title: Survey which inference APIs the router should front
status: To Do
assignee:
  - '@chatswarm'
created_date: '2026-11-16T23:02:00Z'
updated_date: '2026-11-17T05:55:00Z'
labels: []
dependencies:
  - TASK-14
priority: low
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner ask (2026-11-16): find out which upstream inference APIs the router should speak besides OpenAI-compatible. Anthropic's Messages API is the obvious one. The open question is the local engine world: does Strata expose a native API (it already has /props and /models probes beyond OpenAI shape), and does Project Maya? Also worth pricing in: Ollama, llama.cpp's llama-server, vLLM/SGLang, LM Studio, and OpenAI's newer Responses API (versus chat/completions). Deliverable is a decision with consequences, not a survey dump: which 1-2 adapters to build, which to decline, and what the engine-kind seam in EngineRuntime/RouterConfig needs to support them. Sequence AFTER the README updates (TASK-14). The request goes to the external research service once TASK-14 lands; if a request has to be re-issued, re-issue the same one rather than inventing a new one.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Request submitted to the external research service after TASK-14 lands, and its answer mirrored into this task body
- [ ] #2 Candidate APIs compared on what they cost us: streaming shape, tool/thinking blocks, cancellation, context-window signalling, auth, AOT-safe JSON story
- [ ] #3 A decision recorded in SPEC: which adapters are in for v0.2, which are explicitly out, and the engine 'kind' taxonomy that supports both
- [ ] #4 Any new kind gets a fake-handler wire test before real engine wiring
<!-- AC:END -->

## Comments

<!-- COMMENTS:BEGIN -->
created: 2026-11-17T05:26:00Z

---

Assignee changed from the external research service to the agent. Nothing has been dispatched: AC #1 is the dispatch itself, and it is sequenced behind TASK-14, so the next move is whoever submits the request, not the service that will answer it. The service owns a request only once it exists — the board has already used that convention on the tasks whose research actually ran. Dependency on TASK-14 recorded so the ordering is structural rather than a sentence in the description.

---

created: 2026-11-17T05:55:00Z

---

Assignee is the external service again now that the owner has authorised handing this over: the deliverable is a comparison of upstream API shapes and a recommendation of which one or two adapters to build, which is a research deliverable, not local work. Dispatch still waits on TASK-14 by sequencing, so the task stays To Do and carries that dependency.

---
<!-- COMMENTS:END -->
