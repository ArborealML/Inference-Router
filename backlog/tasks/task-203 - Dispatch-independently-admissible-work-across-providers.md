---
id: TASK-203
title: "Dispatch independently admissible work across providers"
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
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
Capture finite published-ticket rounds in existing depth/ordinal and priority/FIFO order; arrivals cannot extend them. Enumerate currently usable built-in routes before cold choices. Pure feasibility checks and resource-aware exact take revalidate live competing predecessors, ticket/config generations and real caps without I/O under gates. Preserve custom scheduler compatibility and narrow measured-switch ordering. Continue after refusal, invalidation and success without restarting the round.

Separate local-group observation from explicit ticket/domain drain intent; retire before reassignment and on queue end. Reuse DispatchWake revision/arm/recheck and deadline hooks. Add bounded blocking-scope/alternative observations to existing tickets/events/monitor. Fixtures cover same/different-model remote progress during local holds, inverse saturation, independent/shared groups, long blocked prefixes, hot arrivals, cancellation, reorder and exact-take races. Amend SPEC §§5.4–5.5/14 and README; no parallel scheduler.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Implementing against main 62f1c79 after independent plan PASS with bounded conditions. Exact predecessor probes remain pure; preserve admission/router/queue lock order, round-local exclusions and wake revision rechecks. Drain intent retires before reassignment; observations are capped, change-only, and cannot wake scheduling. Barrier tests must prove alternate backend receipt before local release. Injected preparation scope remains partial until TASK-206. Scheduling owns queue, selector, local-group and dispatch ranges; TASK-187 owns forwarding/completion. PR #83's additive lifecycle-progress changes remain preserved; PR #86 owns priority parsing. TASK-204/205 remain separate.

Local .NET SDK and Backlog CLI are unavailable; coordinator uses controlled Markdown edits and strict board validation. Focused C# filters and hosted build/test/AOT must be executed before qualification claims. Production preparation scoping remains TASK-206; deployed incident provenance remains TASK-219. No paid inference, hardware mutation or merge authorized.
<!-- SECTION:NOTES:END -->
