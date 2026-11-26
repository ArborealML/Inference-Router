---
id: TASK-197
title: >-
  Bind the launch target so a router-selected tier is verified as exactly as a
  named one
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-26T09:21:00Z'
updated_date: '2026-11-26T10:42:00Z'
labels:
  - scheduling
  - lifecycle
dependencies:
  - TASK-189
priority: high
ordinal: 189000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Conformance at launch is switched on only when the caller named a tier, so a tier the router picked itself — the ordinary case for a context-only request — publishes Ready on a window-only proof. A launch told to start four lanes that comes up measuring eight is certified, emits a completion event, and dispatches work against a capacity nobody asked the provider to start. Bind the expected window and lane count to whatever tier the launch targeted, named or selected, and report a lane mismatch as its own named reason rather than the window-too-small text that tells the operator the context is too small.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A launch targeting a concrete tier fails conformance when the observed lane count differs from that tier's declaration, whether the tier came from the request or from the router's own selection.
- [ ] #2 One test names a tier and one selects it, each observing extra lanes, and both assert no Ready publication, no completion event and no forward.
- [ ] #3 The refusal names the mismatch as expected and observed lane count, distinct from the existing window-too-small reason, and the reason is additive to the §6.1 list.
- [ ] #4 A takeover or retry verifies against the numeric window and lane pair of the launch that actually started, persisted with the durable record and instance identity.
- [ ] #5 The lane-mismatch token joins the closed switch-reason set and reaches the request-path refusal as one token only, with both counts on the feed.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Both call sites pass the targeted tier but set exactProfile only when the caller named one (RouterCore.cs:822, LocalLifecycleGroup.cs:150), so an auto-selected launch certifies on a window-only proof. Freeze the expected window and lane pair at the switch claim, bind it to the launch that actually starts rather than a previous selection or a later probe, and persist that pair with the durable host launch record and instance identity so a takeover verifies against what ran. Add the exact comparison at the two certification doors that lack it: legacy final publication under prove-health and trust mode, and the staged host settle before the route is committed and opened. Leave managed vLLM's existing exact check in EngineRuntime.ManagedProfile.cs alone, and leave the resident lane floor at LocalLifecycleGroup.cs:39-50, PrefixAwareScheduler.cs:96-105, RouterCore.cs:369-375 and 477-483 and EngineRuntime.cs:984-995, pinned by ResidentProfileNoOpTests and LocalLifecycleGroupTests: extra resident lanes still satisfy a narrower tier. Report a lane mismatch as one token added to SwitchReason in WireText.cs, with SPEC 5.5 and 6.1 updated and RefusalCategoryTests walked both ways; both counts stay on the feed, never in the caller sentence, because the reason reaches the request-path 503 body at RouterCore.cs:831-845. Golden-pin the existing window_too_small wording. On a failed exact check the route stays closed, the host record keeps its pending instance identity, the group is not resident, and nothing settles as vacant while the instance runs; automatic fenced disposal is the follow-on task.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The plan review verdict was REVISE; every finding carried into the plan above was checked against main first, including the closed switch-reason set at WireText.cs:92-139 and the reason reaching a caller-visible body at RouterCore.cs:831-845. Persisting the numeric pair so a takeover verifies what ran, and the failed-check state policy, both came from the review. Accepted gaps carried to the follow-on task: automatic fenced disposal of a rejected physical instance, and an idempotent same-instance stop with verified vacancy after cancellation or takeover. Wake: the delta design pass, in flight with the external service.
<!-- SECTION:NOTES:END -->
