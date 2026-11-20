---
id: TASK-57
title: Hand out an immutable capability snapshot, never the live model array
status: Waiting
assignee:
  - '@gpt-6.2-sol-shutdown'
created_date: '2026-11-17T20:41:00Z'
updated_date: '2026-11-20T22:07:00Z'
labels:
  - concurrency
milestone: m-3
dependencies: []
priority: medium
ordinal: 57000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on the handoff of the readiness fixture repair and the reserved switch-claim citation audit, followed by release of the approved allocation fixture to Shutdown. The scheduler uses an immutable roster and public callers receive copies. The [landed mutation test](https://github.com/ArborealML/Inference-Router/blob/bbd8bb9/tests/Arboreal.InferenceRouter.Tests/CapabilityScanTests.cs#L9) covers inputs, getter output and deconstruction. Its source matches 31179e3. The [full Release run](https://github.com/ArborealML/Inference-Router/actions/runs/44816063374) passed 1,798/1,798. The runtime getter mutation also passed in the 122-case run at 4bc9a0a. Allocation measurement and SPEC §5.5 wording remain open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 no published capability array is reachable as a mutable array from any surface (an immutable collection inside the snapshot, nothing escaping it)
- [ ] #2 the scheduler's per-decision snapshot read stays allocation-free, pinned by a test rather than asserted
- [ ] #3 SPEC 5.5 names the snapshot as the only published capability shape, and calls it immutable only if it is one
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Independent review accepted one fixture on 3a8505a. Warm ReadDispatchObservation and ModelRoster access, then measure allocated bytes on the same thread around the snapshot read alone, consuming its reference and scalar results. Expect zero bytes. Separately use the real AutoSwitch=false dispatch path and a recording scheduler returning Wait to verify that it receives the same capability and roster while TryDispatchOnce returns false. Replace roster access with a copy, require a positive allocation delta, restore the source and verify zero again.
<!-- SECTION:PLAN:END -->
