---
id: TASK-197
title: >-
  Bind the launch target so a router-selected tier is verified as exactly as a
  named one
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-26T09:21:00Z'
updated_date: '2026-11-26T10:28:00Z'
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
Conformance at launch is switched on only when the caller named a tier: both launch paths pass an expected profile solely when the request carries an explicit profile option, so a tier the router picked itself — the ordinary case for a context-only request — publishes Ready on a window-only proof. A launch instructed to start four lanes that comes up measuring eight is then certified, a completion event is emitted, and the instance is dispatched against a capacity nobody asked the provider to start. Bind the expected window and lane count to whatever tier the launch targeted, named or selected, and report a capacity mismatch as its own named reason rather than as a window shortage, which today tells the operator the context is too small when the lanes are what differ.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A launch targeting a concrete tier fails conformance when the observed lane count differs from that tier's declaration, whether the tier came from the request or from the router's own selection.
- [ ] #2 One test names a tier and one selects it, each observing extra lanes, and both assert no Ready publication, no completion event and no forward.
- [ ] #3 The refusal names the mismatch as expected and observed lane count, distinct from the existing window-too-small reason, and the reason is additive to the §6.1 list.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Both launch paths decide conformance from an expected profile that exists only when the caller named one: LocalLifecycleGroup.cs:150 derives exactProfile from the request's profile option, and RouterCore.cs:819-822 does the same for the other provider, while the publication gates already compare exactly (EngineRuntime.cs:2064, EngineRuntime.HostLifecycle.cs:154). Carry the targeted tier, named or selected, into the launch record as the expected window and lane count, and have the post-launch proof compare the just-started instance against that record in both providers. Where the measured window matches but the lanes differ, refuse with a new additive reason naming the expected and observed lane count, and leave the existing window-too-small reason for a window shortfall, pinned byte for byte. Keep the lane-floor comparison (Matches at LocalLifecycleGroup.cs:50, PrefixAwareScheduler.cs:99, RouterCore.cs:373 and :479) for an already-resident engine, so an engine with extra lanes still satisfies a narrower request: only an instance this router just started is held to its own tier exactly. Tests: a named-tier launch and a router-selected-tier launch whose fake provider reports extra lanes, each asserting no Ready publication, no completion event, no forward, and the new reason; one case where the resident measures extra lanes and the request proceeds, so the zero-cost target from the containment fix does not regress; and a pin that the existing window-too-small body is unchanged.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
A plan review of the plan above was dispatched to the external reasoning service on 2026-11-26 with the two provider paths and the publication gates named as its evidence. Wake: that review landing; nothing is implemented until it does. The new lane-mismatch reason is wire-visible and needs the usual wire review before shipping. The failed-exact-check cleanup this plan deliberately does not settle sits on the follow-on task.
<!-- SECTION:NOTES:END -->
