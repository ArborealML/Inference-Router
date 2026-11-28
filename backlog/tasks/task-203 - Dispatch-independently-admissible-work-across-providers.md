---
id: TASK-203
title: Dispatch independently admissible work across providers
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T12:43:07Z'
labels: []
milestone: m-17
dependencies: []
priority: high
ordinal: 203000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Eligible requests must progress while an independent provider is occupied. Preserve original priority and enqueue order among requests competing for capacity. Provider backoff, scoped ownership and remaining diagnostic cases retain their acceptance here.
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
Post to the normal chat endpoint with a delegated remote full and a router-managed local engine. Hold one remote body, then cover warm and cold local service for both the held model and a different one. Verify ticket, model, bytes, lifecycle operations and terminal before release. Count duplicate writes after cleanup. Reuse the two-domain preparation and shared-thread controls. Account backoff requires the actual provider contract.

Exercise pure probes separately from full rounds. Count one accepted drain intent per ticket and domain and demonstrate retirement before a replacement reservation, including caller expiry. Retain pending physical uses until confirmed cleanup. Preserve original deadlines and competitor ordering.

Publish a bounded immutable observation of the blocker and evaluated alternatives. Verify ticket, queue membership, configuration, route and group generations before publication. Derive status, events and monitor output from that observation. Indicate truncation and unevaluated alternatives, suppress equivalent reports, clear obsolete ones and verify no diagnostic wake loop or secret exposure.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #88](https://github.com/ArborealML/Inference-Router/pull/88), head cb595b2d96646fe038261ff57f6041e813aacd6f, implements finite rounds, alternatives, exact competing order, explicit drain ownership and bounded wait scope. [Source mappings](../milestones/wave1-publication-map.json) connect reviewed 5beaaa7..9f023f3 to the identical published tree under TASK-220's explicit provenance exception.

Last-slot precedence passed at 9f023f3 (263 tests). [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112027567) passed 2,160 tests, Linux AOT and smoke. SPEC v0.12.163 records the scope.

The dispatcher landed in the [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325). [PR 94](https://github.com/ArborealML/Inference-Router/pull/94) exercised held preparations in either private domain for shared and distinct models. Another thread progressed under shared credit. Commercial backoff, explicit ownership across reassignment and full operator diagnostics retain their acceptance here.

[Provider progress](https://github.com/ArborealML/Inference-Router/pull/101) at f7529cd passed 139 focused checks. Four normal HTTP cases served warm and cold managed requests for the same or a different model before the remote slot released. [Merged qualification](https://github.com/ArborealML/Inference-Router/actions/runs/46146451622) passed 2,423 tests. At 8ebb7b4, 141 focused checks included ticket/event HTTP agreement and zero new claims when a predecessor wins. Review accepted these as model-scoped, unevaluated candidates. Full profile eligibility and diagnostic publication under unrelated engine churn remain unqualified. Paid-account backoff stays open.
<!-- SECTION:NOTES:END -->
