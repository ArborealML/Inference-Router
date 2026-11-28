---
id: TASK-203
title: "Dispatch independently admissible work across providers"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
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
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Start with complete finite snapshot rounds (bounded by captured tickets or sequence cutoff; arrivals do not extend the round indefinitely). Per-model caps are not a global bound. Enumerate ready eligible alternatives; pure probes and resource-aware exact take revalidate competing predecessors, queue identity, config and actual caps. Preserve existing outer queue order and decision-25 fairness; no priority-dependent route economics. Keep network/disk/observer work outside shared gates. Reuse DispatchWake; measure traversal before chunking. Capture barriers cover long blocked prefixes and independent progress.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Static findings: PrefixAwareScheduler, ModelQueue, RouterCore, LocalLifecycleGroup.TryReserve and DispatchWake at the reviewed main. Deployment-specific busy-resident waiting needs separate provenance; do not claim the actual incident is reproduced until TASK-219. Production injected preparation scoping is covered by TASK-206.
<!-- SECTION:NOTES:END -->
