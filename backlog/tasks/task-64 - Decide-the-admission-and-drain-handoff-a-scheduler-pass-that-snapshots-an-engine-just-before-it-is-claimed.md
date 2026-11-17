---
id: TASK-64
title: >-
  Decide the admission and drain handoff: a scheduler pass that snapshots an
  engine just before it is claimed
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
labels:
  - scheduling
  - architecture
dependencies: []
priority: medium
ordinal: 64000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Recorded as an accepted gap in SPEC 15 by the fix set for fd5e0cd; this row is the work that gap names. A scheduler pass snapshots an engine as healthy and idle; a switch then claims that engine under the transition gate; the pass dispatches a no-switch ticket to it without WaitForModel, so the request runs against an engine about to be stopped. Nothing here can patch it: correctness needs the admission/drain design EngineLifecycleCoordinator.cs:8-12 already describes and defers, where a drain marks the engine unavailable at admission and re-queues what already arrived. The same shape is documented there for the no-capacity case, so this row exists so the gap has a home rather than a comment. Acceptance is a shape, not a patch: what an in-flight pass is told when an engine is claimed under it, and what happens to a ticket already snapshot-approved.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC 15 carries the accepted gap with this task named and the fd5e0cd review as its source
- [ ] #2 the admission/drain shape is written as a decision with the in-flight-pass rule stated
- [ ] #3 a test shows a ticket approved by an earlier pass is not dispatched to an engine claimed after that pass
<!-- AC:END -->
