---
id: TASK-57
title: Hand out an immutable capability snapshot, never the live model array
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T20:41:00Z'
labels:
  - concurrency
dependencies: []
priority: medium
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The versioned snapshot 20a06fa introduced is only atomic while it stays inside the router. EngineRuntime.cs exposes the snapshot's array directly — LoadedModels returns the array field of the published snapshot (:104-105) — so any in-process reader (the scheduler's view, an admin handler, the live view) can write into a published capability list without a version bump, and the scan's stability check cannot see it. That is the same torn-state class the snapshot was built to close, reached from the read side. The fix is to publish an immutable or copied shape; the constraint that matters is the request hot path, so pin the allocation behaviour rather than assuming a copy is free.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 no published capability array is reachable as a mutable array from any surface (an immutable collection inside the snapshot, nothing escaping it)
- [ ] #2 the scheduler's per-decision snapshot read stays allocation-free, pinned by a test rather than asserted
- [ ] #3 SPEC 5.5 names the snapshot as the only published capability shape, and calls it immutable only if it is one
<!-- AC:END -->
