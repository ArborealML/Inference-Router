---
id: TASK-203
title: Dispatch independently admissible work across providers
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:56:00Z'
labels: []
milestone: m-17
dependencies: []
priority: high
ordinal: 203000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
An available provider must serve eligible requests while an independent provider is occupied. Priority and original enqueue order govern requests competing for the same capacity. The dispatcher integration is on main. The remaining work covers reverse blocking, provider backoff, scoped ownership and the operator view.
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
Reuse the merged finite-round and domain-preparation fixtures. Hold remote capacity while eligible same-model and distinct-model requests finish locally. Attach account-scoped backoff checks when the provider rate-limit contract lands. Assert original ticket identity, exactly one send, and the genuine shared-cap control.

Exercise a pure eligibility probe and a ticket that changes domains before first write. Count acquired, retired and outstanding drain intents at each boundary. Carry the blocking resource, alternative availability and wait reason from the same bounded snapshot through status, events and the monitor. Reuse existing wake and ordering checks.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #88](https://github.com/ArborealML/Inference-Router/pull/88), head cb595b2d96646fe038261ff57f6041e813aacd6f, implements finite rounds, alternatives, exact competing order, explicit drain ownership and bounded wait scope. [Source mappings](../milestones/wave1-publication-map.json) connect reviewed 5beaaa7..9f023f3 to the identical published tree under TASK-220's explicit provenance exception.

Independent plan/source review passed after the cross-engine last-thread-slot race correction: release shares the router gate and competing route evidence is revalidated. A post-probe barrier verifies earlier-ticket precedence. Accepted drain scope stays visible. Local Release verification at 9f023f3 passed 263 tests with no failures/skips; fixture/diagnostic and injected cap/window corrections retained assertions. SPEC v0.12.163 states scope.

[Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112027567) passed on this actual head: 2,160 tests, zero failed/skipped, build, Linux AOT/config smoke, strict board and release harness. The synthetic merge tree equals the head tree.

The dispatcher and its qualified fixtures landed in the [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325). [PR 94](https://github.com/ArborealML/Inference-Router/pull/94) exercised held preparations in either private domain for shared and distinct models. Its shared-thread control identified the limiting credit and allowed a separate thread through. Commercial backoff, explicit ownership across reassignment and full operator diagnostics retain their acceptance here.

Review of the remaining reverse-dispatch and operator checks is pending. The existing full-remote fixture configures both endpoints as delegated. Add managed-local captures behind a saturated remote for warm and cold cases. Capture the alternative routes and limiting scope from the same scheduling observation.
<!-- SECTION:NOTES:END -->
