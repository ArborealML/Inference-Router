---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: Preparing
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-19T09:46:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-68
  - TASK-130
references:
  - src/Arboreal.InferenceRouter/Engines/EngineRuntime.cs
  - src/Arboreal.InferenceRouter/RouterCore.cs
ordinal: 116000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Map remaining Defiant verified-dispatch criteria against the landed offline path.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Aliases and omitted defaults resolve to the same effective target. Two profiles differing in an effective option remain distinct even when their model, context and slot counts match.
- [ ] #2 A joiner interprets its selection in the active claim’s captured configuration. A changed default or changed profile options in a newer configuration cannot silently retarget that claim.
- [ ] #3 For each supported engine family, fixtures verify all profile-distinguishing settings against one serving instance. Missing, conflicting or stale observations withhold readiness and prevent dispatch.
- [ ] #4 After an instance restart or port rebind between proof and dispatch, a paused waiter rechecks its reservation. Neither a stale pooled connection nor the replacement listener receives the request under the old proof.
- [ ] #5 A positive control delivers the expected upstream model ID to the verified instance. Tests distinguish an explicit profile request from ordinary inference fitting an already proven resident profile.
- [ ] #6 After the final readiness check, rebind the endpoint before the first upstream write. The replacement receives zero bytes under the previous proof. SPEC names the instance-bound transport or reservation and its retirement rule.
- [ ] #7 Freeze the final rewritten body and credential policy with the invocation. Changing either after preparation refuses delivery before the first write. An unchanged invocation sends the expected bytes to the verified route.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Drive one real selected ticket through exact take and the verified issuer. Bind its effective target, final rewritten bytes and credential policy. Publish one immutable fake resident tuple containing the exact route, process, epoch, generation, config, plan, canonical model, effective tier, measured window and total slots. Named and resolved-default tiers, including ordinary reuse of a named resident, require a fitting positive window and positive measured slots. Public gauges and roster can veto only. After retirement require a fresh generation. Serialize issuer closure against first nonempty write; hold the write with caller and router tokens live and verify closure prevents bytes. Release accounting only on typed no-issue or confirmed retirement; retain unknown use even after a pre-header failure. Treat parsed Docker projections and digest as offline intent paired with a separate inert resident. Preserve startup and mutation guards. Engine-stop integration requires an explicit issuer bridge.

Publish one immutable sender with the ready preparation. Its exact invocation borrows it and retains the existing cancel/submit decision. Before headers the single transport worker exposes independent primary and terminal outcomes. End response borrowing in finally; finish preparation on every path. Release submitted capacity once only after conclusive no-use or retirement and successful preparation cleanup. After exact peer receipt, hold retirement through a pre-header failure: one primary terminal, zero successor bytes; failed retirement emits one redacted cleanup fault and retains use, confirmed cleanup releases once. Preserve standalone transport behavior. A permanent binding refusal settles only the current exact ticket once without relying on a timer; stale preparation cannot settle a replacement.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Binding review, 118 Release cases and Linux x64 AOT passed on 1bd351d. Cleanup review passed e05f5c9; six cases passed, one silent-peer cancellation timed out. Fix 9e1a9df passed both cancellation rows and review. [PR #20](https://github.com/ArborealML/Inference-Router/pull/20) at 0e6343a passed integration review, the focused health-check test and hosted CI. Changing only the command invalidates the prior route with zero daemon calls.

[PR #20](https://github.com/ArborealML/Inference-Router/pull/20) merged at 4537b1a after hosted CI and final SPEC reconciliation review passed. All 17 affected source and test blobs retained their qualified identities. Production enrollment remains separate.
<!-- SECTION:NOTES:END -->
