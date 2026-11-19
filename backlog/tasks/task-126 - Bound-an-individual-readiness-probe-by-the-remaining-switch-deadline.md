---
id: TASK-126
title: Bound an individual readiness probe by the remaining switch deadline
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T20:43:00Z'
updated_date: '2026-11-19T10:59:00Z'
labels: []
milestone: m-4
dependencies: []
priority: high
ordinal: 124000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The independent review of the remaining readiness controls wakes Shutdown for implementation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Hold a state probe and then a models probe separately past the remaining switch budget. Each attempt settles within the named bound and produces one terminal result.
- [ ] #2 A delayed successful reply after timeout cannot publish readiness or revive a retired attempt. A healthy alternate can still make progress.
- [ ] #3 Return an enrolled refusal through RunSwitchAsync after expiry or shutdown has won. The caller retains the original terminal cause and the feed records one terminal event, with no second capability change, completion, or dispatch wake.
- [ ] #4 Exercise successful guarded publication with hooks that verify both short locks are released. A joined waiter woken by shutdown or expiry receives its committed result only after the outcome event is delivered.
- [x] #5 Hold the committed outcome event and stop the engine with two joined waiters on the same tier. After event delivery only the waiter whose minimum fits the committed measured window receives Ready. Neither waiter can dispatch.
- [ ] #6 For explicit named and resolved-default enrolled tiers, test a fitting window on the verified instance and route in budget. Omitted or zero measured total slots refuse publication; positive measured total slots on that binding permit readiness.
- [x] #7 At absolute deadline equality, present otherwise qualifying window and positive slot evidence. The guard refuses publication, completion and permits. In the in-budget success control the hook receives the committed proof outside both short locks under the retained reservation.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Pre-code review dispatched on 33debba. Hold the retired response on A until an independent healthy engine B finishes a request. Exercise typed enrolled refusal through real runtime unwind after stop wins. Use five in-budget slot inputs: omitted and zero for explicit and resolved-default tiers, plus resolved-default positive. Fix the verified route and fitting window. During the default positive proof hook, witness independent acquisition of both short locks while the outer reservation remains held. Reuse existing named success, equality and joined delivery evidence.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Held state and models probes stayed pending at 99 ms and settled once at 100 ms before release. All three rows passed on 90c5449 and 4bc9a0a. Legacy readiness and local HTTP cleanup landed in [PR #4](https://github.com/ArborealML/Inference-Router/pull/4). The two-ticket control in [PR #8](https://github.com/ArborealML/Inference-Router/pull/8) passed both rows: the stopped engine granted neither ticket a send, and the open engine accepted one exact POST. For the offline enrolled case, Lifecycle owns coordinator, staging and the single recovered-launch fixture. Shutdown owns EngineRuntime and its host lifecycle partial. Build on the exact host child, reuse its launch-owned deadline and the existing guarded CAS and delivery signal, and consume the typed terminal outcome before generic launch failure. Preserve production capacity gates.

Runtime review of df9fdef found Ready survives a refused Open. Preserve that historical result: withdrawing its proof can send a woken waiter back to retry. The offline issuer checks exact open authority at mint and first write. Production RouterCore supplies no enrollment. Require both checks when integrating enrolled dispatch and admission. Synchronous Open takes host admission before transition. At 8af4cdd the deadline fixture passed 1/1 with a closed completion hint, an open hint on success and zero permits on expiry. [PR #23](https://github.com/ArborealML/Inference-Router/pull/23) includes the post-Open notification change. Its fault control passed on a99b91c, preserving committed readiness, actual opening and the original exception.
<!-- SECTION:NOTES:END -->
