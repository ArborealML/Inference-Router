---
id: TASK-206
title: Extract host manager authority and scope preparations
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-18
dependencies:
  - TASK-64
  - TASK-68
  - TASK-130
priority: high
ordinal: 206000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Establish the smallest in-process manager seam around EngineRuntime, LocalLifecycleGroup, capability publication and exact admission. Ordinary construction cannot compose lifecycle groups and injected admission. Reuse verified issuer/transport foundations; production commissioning and remote transport are later acceptance, not prerequisites to this seam.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One compiled contract exposes bounded snapshot, accept/refuse, execute/cancel and disposition; combined role uses in-process calls and one physical owner.
- [ ] #2 Ordinary composition exercises enrolled admission and lifecycle together against controlled fakes; unsupported or uncommissioned domains refuse mutation and exact send.
- [ ] #3 Preparation slots, waiting-owner preference and cleanup are scoped to actual domains; holding one does not delay another.
- [ ] #4 Snapshot is advisory; accepted immutable operation/body, recipe, instance and domain generations bind one attempt; stale or conflicting IDs refuse.
- [ ] #5 Pure probes acquire no drain intent; at most one accepted intent per ticket/domain, explicitly retire before reassignment.
- [ ] #6 Fixture restart, late cleanup and lost acknowledgment do not release physical ownership prematurely; no second prioritized queue or remote actuation.
- [ ] #7 SPEC §§4–7, 12, 14 and AOT fixtures define the seam; existing standalone and legacy forwarding remain compatible.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse TASK-64/68/130 fixtures and the existing issuer/retirement primitives. Establish an ordinary composition and minimal enrollment/issuer interface with capture fixtures, before TASK-25/118/110 production integration. Define stable IDs, immutable generations and bounded receipt retention; reject stale execution after receipt retirement. TASK-77 owns remote role/transport. Supply the shared typed manager snapshot and preparation/drain reason fields to existing status/events and CLI/monitor; no extra UI architecture. Keep direct manager admission and router pending ownership distinct.

Group aliases by verified physical authority and incarnation. Scope preparation capacity, pending-owner preference and cleanup to that authority. Keep one attempt per ticket through retirement. Revalidate the same immutable operation before first write; preserve existing retry fences. Test held preparation in either direction, shared thread limits and fail-closed enrollment.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The first in-process increment is [PR 94](https://github.com/ArborealML/Inference-Router/pull/94). Controlled fixtures cover the original cold request, independent same- and different-model progress in both directions under held preparation, physical aliases, invalid enrollment and intentional shared-thread limits. Plan review identified alias identity, late retirement, and all three preparation gates. Review of 0d216f7 identified blocked preparations hiding eligible cold routes, stale residency after withdrawal and reusable completion handles. Commit 5b05048 addresses these along with cancellation delivery and includes six targeted regression cases. All 279 focused Release tests passed at that commit, zero skipped. Remaining work includes durable disposition/recovery qualification and completing the manager observation surface. Production enrollment, peer transport and actual two-host acceptance belong to the dependent integration tasks.
<!-- SECTION:NOTES:END -->
