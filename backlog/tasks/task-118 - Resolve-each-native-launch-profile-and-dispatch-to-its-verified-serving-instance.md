---
id: TASK-118
title: >-
  Resolve each native launch profile and dispatch to its verified serving
  instance
status: In Progress
assignee:
  - '@gpt-6.2-sol-scheduling'
created_date: '2026-11-18T18:56:00Z'
updated_date: '2026-11-19T09:07:00Z'
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
Correct verified transport cleanup accounting and fault reporting before offline dispatch lands.
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
Binding review passed at 1bd351d for raw model, frozen request, captured provider config/endpoint and resident identity. Credential epoch, Authorization/Host cardinality and effective URI checks verified. Additional permutations optional. Qualification: 118 Release cases and Linux x64 Native AOT. Two cleanup defects from 43b64d5 remain. Release capacity once after confirmed retirement and successful preparation cleanup despite a response-disposal fault, and report it. Preserve both stream-close and permit-retirement exceptions. Unknown retirement or failed preparation cleanup retains capacity. Scheduling owns both focused controls.
<!-- SECTION:NOTES:END -->
