---
id: TASK-203
title: "Dispatch independently admissible work across providers"
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-27'
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
- [ ] #1 With a local slot or lifecycle operation held, a ready remote request of the same model and of a different model reaches its capture backend before release.
- [ ] #2 Repeat with remote capacity/backoff held and with two independent private resource groups. A real shared cap still blocks and reports its scope.
- [ ] #3 Select available alternatives before unavailable or activatable routes; definitive refusal permits reconsideration without duplicate grants or losing original ticket age.
- [ ] #4 A blocked prefix and continuously replenished early queue cannot hide later eligible work. Test complete rounds, cancellation and ordering changes at exact removal.
- [ ] #5 Pure eligibility never acquires a drain owner. An accepted preparation is scoped to one domain and ticket; retire it before reassignment.
- [ ] #6 Reuse the revision/arm/recheck wake protocol; arrivals, release, readiness, refusal, cancellation and due deadlines wake exactly as needed without busy-spin.
- [ ] #7 SPEC §§5.4–5.5 and 14 describe independent progress and priority-only ordering; focused tests preserve exact admission, terminal-before-waiter and existing caps.
- [ ] #8 Finite rounds terminate despite arrivals; resource-aware exact take cannot bypass an earlier currently admissible competitor for the same resource or spin on a permanently blocked predecessor.
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
Candidate 9f023f30e9b5660bafed64c8fccd1b9f3accf3a7 against main 62f1c79 implements finite rounds, route reconsideration, exact competing order, explicit local drain ownership and bounded wait scope. Independent plan review preceded code. Source range 5beaaa7..9f023f3 independently passed after a cross-engine last-thread-slot race correction: ordinary release shares the router gate, and only competing route evidence is revalidated. A post-probe readiness barrier verifies the earlier ticket wins. Accepted drain scope now remains visible over unusable alternatives.

Release verification at exact 9f023f3: 263 passed, zero failed/skipped across independent progress, queue/selector, lifecycle groups, switch cost/fairness, exact admission, ticket lifetime, monitor, reload, lifecycle authority and configuration fixtures. Build/analyzers passed. Earlier fixture/diagnostic failures and injected live-cap/window precedence regressions were corrected without weakening assertions. SPEC v0.12.163 and README state supported scope.

AC2 remains partial for commercial backoff/account controls; AC5 remains partial for injected preparation scoping owned by TASK-206; AC9 reports current/first-blocker scope, not the full available-route set. Preserve PR #83 progress and PR #86 parsing; TASK-187 owns observation, TASK-204/205 separate corrections. Coordinator next publishes the reviewed candidate, checks exact-head hosted build/test/AOT and reconciles integration. Backlog CLI unavailable; controlled Markdown and strict checker substitute. No deployed incident, hardware or commercial qualification; not Done.
<!-- SECTION:NOTES:END -->
