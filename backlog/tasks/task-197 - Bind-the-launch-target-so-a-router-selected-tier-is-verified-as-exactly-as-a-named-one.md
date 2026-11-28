---
id: TASK-197
title: >-
  Bind the launch target so a router-selected tier is verified as exactly as a
  named one
status: To Do
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-26T09:21:00Z'
updated_date: '2026-11-26T10:58:00Z'
labels:
  - scheduling
  - lifecycle
dependencies:
  - TASK-189
  - TASK-195
  - TASK-206
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
Both design passes returned REVISE, with corrections folded in here and checked against main. Put the immutable expected window and lane pair on the durable host operation (HostMutation, Engines/Lifecycle/HostReservation.cs:13-16) as a nullable nested member, not JsonRequired: only Epoch, Owner and Pending are required today, so a required new field would make every pre-existing checksummed journal unreadable. Require both values positive for a new Launch, bound every comparison to Pending.Id and Epoch plus LaunchedContainerId, and do not touch the checkpoint plan, whose serialized bytes are hashed into its digest: that needs a versioned format, not a schema edit. The state disallows unmapped members, so a new field breaks rollback to the previous binary: state that rule in SPEC. Freeze the pair at the switch claim, carry it on its own operation, and verify exactly at the two certification doors: legacy final publication and the staged settle before the route commits and opens. Leave the resident lane floor and managed vLLM's exact check alone. Cross-target trap: after an old applied launch A and a new target B, verify against A's persisted pair, never B's tier, and a restarted process under the same container id is not the same incarnation — require a process or start generation, or fail closed. Land as two commits: the durable record with migration and restart tests, then freeze-and-verify with one new token added to the closed switch-reason set, the counts feed-only, and a golden pin on window_too_small. The durable refused identity, the retry bound and disposal stay on the follow-on task.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The first pass found the window-only certification and the closed-set leak into a caller-visible body; the second found the record migration hazard, the cross-target comparison trap and the unbounded retry. Tests required before merge: an old checksummed reservation without capacity that opens but cannot authorize a Ready publication, in legacy and enrolled shapes, across restart, takeover and a same-id restarted process; and a lane mismatch, named and selected, asserting the caller token, feed-only counts, and no Ready or forward.
<!-- SECTION:NOTES:END -->
