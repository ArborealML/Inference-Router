---
id: TASK-57
title: Hand out an immutable capability snapshot, never the live model array
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T23:32:00Z'
labels:
  - concurrency
milestone: m-3
dependencies: []
priority: medium
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The remaining work is to reconcile SPEC §5.5 with the published immutable snapshot. [PR #66](https://github.com/ArborealML/Inference-Router/pull/66) landed the allocation fixture at a9a3c68. In Release, 256 warmed snapshot and roster reads allocated zero managed bytes on the measuring thread. A forced copy allocated 8,192 bytes; restoration returned to zero. A separate scheduler Wait callback verified the same capability and roster references. All three [post-merge checks](https://github.com/ArborealML/Inference-Router/actions/runs/44836939149) passed. The [landed mutation test](https://github.com/ArborealML/Inference-Router/blob/bbd8bb9/tests/Arboreal.InferenceRouter.Tests/CapabilityScanTests.cs#L9) covers constructor and initializer inputs, getter output and deconstruction.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 no published capability array is reachable as a mutable array from any surface (an immutable collection inside the snapshot, nothing escaping it)
- [x] #2 the scheduler's per-decision snapshot read stays allocation-free, pinned by a test rather than asserted
- [ ] #3 SPEC 5.5 names the snapshot as the only published capability shape, and calls it immutable only if it is one
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Independent review accepted one fixture on 3a8505a. Warm ReadDispatchObservation and ModelRoster access, then measure allocated bytes on the same thread around the snapshot read alone, consuming its reference and scalar results. Expect zero bytes. Separately use the real AutoSwitch=false dispatch path and a recording scheduler returning Wait to verify that it receives the same capability and roster while TryDispatchOnce returns false. Replace roster access with a copy, require a positive allocation delta, restore the source and verify zero again.
<!-- SECTION:PLAN:END -->
