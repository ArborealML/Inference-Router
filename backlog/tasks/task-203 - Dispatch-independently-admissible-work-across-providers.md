---
id: TASK-203
title: Dispatch independently admissible work across providers
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T12:06:00Z'
labels: []
milestone: m-17
dependencies: []
priority: high
ordinal: 203000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Eligible requests must progress while an independent provider is occupied. Preserve original priority and enqueue order among requests competing for capacity. Remaining evidence covers reverse blocking, provider backoff, scoped ownership and operator diagnostics.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 With a local slot or lifecycle operation held, a ready remote request of the same model and of a different model reaches its capture backend before release.
- [ ] #2 Repeat with remote capacity/backoff held and with two independent private resource groups. A real shared cap still blocks and reports its scope.
- [x] #3 Select available alternatives before unavailable or activatable routes; definitive refusal permits reconsideration without duplicate grants or losing original ticket age.
- [x] #4 A blocked prefix and continuously replenished early queue cannot hide later eligible work. Test complete rounds, cancellation and ordering changes at exact removal.
- [ ] #5 Pure eligibility never acquires a drain owner. An accepted preparation is scoped to one domain and ticket; retire it before reassignment.
- [x] #6 Reuse the revision/arm/recheck wake protocol; arrivals, release, readiness, refusal, cancellation and due deadlines wake exactly as needed without busy-spin.
- [x] #7 SPEC §§5.4–5.5 and 14 describe independent progress and priority-only ordering; focused tests preserve exact admission, terminal-before-waiter and existing caps.
- [x] #8 Finite rounds terminate despite arrivals; resource-aware exact take cannot bypass an earlier currently admissible competitor for the same resource or spin on a permanently blocked predecessor.
- [ ] #9 Status/events and CLI/monitor identify exact blocking resource or shared cap, available alternatives and wait/refusal scope using existing bounded read models.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Post to the normal chat endpoint with a delegated remote full and a router-managed local engine. Hold one remote body, then cover warm and cold local service for both the held model and a different one. Observe the original ticket, model, bytes, lifecycle operations and terminal disposition before release. Check again after cleanup for duplicate writes. Reuse the two-domain preparation and shared-thread controls. Account backoff requires the actual provider contract.

Exercise pure probes separately from full rounds. Count one accepted drain intent per ticket and domain and demonstrate retirement before a replacement reservation, including caller expiry. Retain pending physical uses until confirmed cleanup. Preserve original deadlines and competitor ordering.

Publish a bounded immutable observation of the blocker and evaluated alternatives. Verify ticket, queue membership, configuration, route and group generations before publication. Derive status, events and monitor output from that observation. Indicate truncation and unevaluated alternatives, suppress equivalent reports, clear obsolete ones and verify no diagnostic wake loop or secret exposure.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #88](https://github.com/ArborealML/Inference-Router/pull/88), head cb595b2d96646fe038261ff57f6041e813aacd6f, implements finite rounds, alternatives, exact competing order, explicit drain ownership and bounded wait scope. [Source mappings](../milestones/wave1-publication-map.json) connect reviewed 5beaaa7..9f023f3 to the identical published tree under TASK-220's explicit provenance exception.

The last-thread-slot race correction revalidates competing routes under the router gate. A post-probe barrier verifies precedence. Release at 9f023f3 passed 263 tests. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112027567) passed 2,160 tests, Linux AOT and smoke. SPEC v0.12.163 records the scope.

The dispatcher and its qualified fixtures landed in the [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325). [PR 94](https://github.com/ArborealML/Inference-Router/pull/94) exercised held preparations in either private domain for shared and distinct models. Its shared-thread control identified the limiting credit and allowed a separate thread through. Commercial backoff, explicit ownership across reassignment and full operator diagnostics retain their acceptance here.

Plan review at f66ae74 verified that both endpoints in the existing full-remote fixture are delegated. It also found the wait publication lacks the originating route and group revisions. The revised plan exercises actual managed ingress and stale-observation rejection.
<!-- SECTION:NOTES:END -->
