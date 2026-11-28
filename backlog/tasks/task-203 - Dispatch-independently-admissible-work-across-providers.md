---
id: TASK-203
title: "Dispatch independently admissible work across providers"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 203000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Fix the existing dispatcher before clustering. A full preferred route masks free alternatives; one candidate per model hides runnable followers; a successful dispatch restarts queue traversal. Preserve priority and original enqueue order for competing work, not across independent resources.
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
Capture finite published-ticket rounds in existing order, excluding later arrivals. Prefer usable built-in routes. Pure probes and exact take revalidate competing predecessors, generations and caps without I/O under gates. Preserve custom schedulers and measured-switch ordering. Continue after refusal, invalidation and success.

Separate observation from explicit ticket/domain drain intent; retire on reassignment/end. Reuse revision/arm/recheck wake and deadlines. Add bounded wait observations; fixture independent/shared groups, remote progress during holds, blocked prefixes, hot arrivals, cancellation and exact-take races. Amend SPEC/README.

Independently reviewed integration plan: isolated actual-ancestry merges of verified 205/203, 187 and 202 receipts; preserve PR #86, keep #83 separate. Resolve additive wait/upstream ticket projections and SPEC entries only. Inspect automatic source merges; run the affected test union including exact admission and review the pinned merge. No main merge.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Published [PR #88](https://github.com/ArborealML/Inference-Router/pull/88), head cb595b2d96646fe038261ff57f6041e813aacd6f, implements finite rounds, alternatives, exact competing order, explicit drain ownership and bounded wait scope. [Source mappings](../milestones/wave1-publication-map.json) connect reviewed 5beaaa7..9f023f3 to the identical published tree under TASK-220's explicit provenance exception.

Independent plan/source review passed after the cross-engine last-thread-slot race correction: release shares the router gate and competing route evidence is revalidated. A post-probe barrier verifies earlier-ticket precedence. Accepted drain scope stays visible. Local Release verification at 9f023f3 passed 263 tests with no failures/skips; fixture/diagnostic and injected cap/window corrections retained assertions. SPEC v0.12.163 states scope.

[Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/46112027567) passed on this actual head: 2,160 tests, zero failed/skipped, build, Linux AOT/config smoke, strict board and release harness. The synthetic merge tree equals the head tree.

AC2 remains partial for commercial account/backoff, AC5 for TASK-206 preparation scoping, AC9 for full available-route diagnostics. Preserve PR #83 progress and #86 parsing; 187/204/205 own their corrections. Source landed in the [main integration](https://github.com/ArborealML/Inference-Router/commit/1bf85d3495ab2517a921d78013ffe69bbefb4325).
<!-- SECTION:NOTES:END -->
