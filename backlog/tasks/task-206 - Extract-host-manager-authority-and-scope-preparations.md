---
id: TASK-206
title: "Extract host manager authority and scope preparations"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-6
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
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
TASK-203 fixes traversal separately; coordinate exact-take and wake semantics.

Production enablement needs TASK-25/118/110/190/197/198; this seam can land disabled for uncommissioned domains. TASK-190/198 reuse one operation/disposition authority, not competing recovery controllers. Existing fixtures/same-host evidence are not production enrollment. External resource exclusivity is a deployment prerequisite, not sandboxing or provisioning.
<!-- SECTION:NOTES:END -->
