---
id: TASK-9
title: 'External comparison: AIR against NVIDIA Personal-AI-Router'
status: Done
assignee:
  - '@chatswarm'
created_date: '2026-11-16T20:46:00Z'
updated_date: '2026-11-17T08:06:00Z'
labels:
  - external
dependencies: []
ordinal: 9000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An external comparison pass has been dispatched to compare our backend with NVIDIA's Personal AI Router capability by capability, with the bar being that our backend is at least as robust. It also inventories their UI for our planned Blazor live view and lists what their router cannot do (which is why we are building ours).

**Result (external review; the full digest stays outside the repository)**
Pinned review: this repo at `d3605fc136e` vs NVIDIA Personal-AI-Router at `1de8439a3811`,
source only.

**This router is not yet as robust as PAIR, even single-host.** It already leads on the axes PAIR
does not serve: request priority and admission, serving profiles / context escalation, prefix
affinity, per-model queues, generic endpoints, engine API keys, multiple instances per engine type.

What PAIR has that we lack: URI handling correctness, a commit boundary for in-flight work, request
and body bounds, per-phase timeouts, dispatch progress that cannot starve, aging, proven
health/context/capability signals, owned lifecycle with drain + recovery, switch coalescing, a
stable error contract, atomic config reload with version pins, auth/TLS/rate caps, bounded
telemetry with checkpointing, and restart generations / terminal ingress / journals.

PAIR's UI is the reference for a live view: shell+status, overview/settings, workload rail with
filters and topology, node cards, GPU/VRAM/CPU/RAM gauges and trends, engine/model controls,
endpoint copy, pairing, service/log/reset, actionable errors. Borrow the rail, filters, topology,
endpoint copy and actionable errors; make capacity/profile/readiness and queue/TTFT/throughput
trends engine-first; skip the installer, model deletion, pairing, autostart/update/wipe.

Fix order: P0 URI + readiness proof + dispatch progress → transport bounds → switches + error
contract → owned lifecycle/recovery (TASK-1) → security + tests. P1 version pins, telemetry,
durability, fairness, Blazor. P2 multi-host.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Capability and robustness matrix with evidence from both codebases
- [x] #2 Every gap in our backend turned into a task with acceptance criteria
- [x] #3 UI screens rated borrow / borrow-with-changes / skip
- [x] #4 The event-stream and status additions needed to make the live view cheap later are listed
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Proof: this was a request to the external research and review service, and its answer is mirrored in the repository rather than in a transcript - the disposition row in [SPEC 15 at line 1352](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1352) (robustness and UI comparison against the other router), which is what the code and SPEC follow. Where the answer was not adopted, the reason is in the same row.
<!-- SECTION:NOTES:END -->
