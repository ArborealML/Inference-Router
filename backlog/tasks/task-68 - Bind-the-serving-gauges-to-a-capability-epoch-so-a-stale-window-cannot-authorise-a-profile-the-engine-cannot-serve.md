---
id: TASK-68
title: >-
  Bind the serving gauges to a capability epoch, so a stale window cannot
  authorise a profile the engine cannot serve
status: To Do
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:11:00Z'
updated_date: '2026-11-18T02:47:00Z'
labels:
  - concurrency
  - scheduling
dependencies: []
priority: high
ordinal: 68000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The context-window, slot and profile-name gauges are three independent volatiles (`EngineRuntime.cs:61-63`, written :436-440, read at :543 and :801, and by the scheduler at `PrefixAwareScheduler.cs:85-86`) with no version relation to the capability snapshot, so a reader can mix epochs. The plan review's counter-example survives against this code: a 128k window measured for the previously loaded model, plus a fresh snapshot for an 8k model, lets `WindowFits` authorise a 32k profile and the scheduler dispatches into an engine that cannot hold the context. A zero window means "the engine never told us" and today fits every profile, which widens eligibility rather than protecting it. This is fencing the **reads**, not the publications — TASK-63 closed the publications.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the window, slot count and profile name are read as one value whose epoch is known, not as three separate reads
- [ ] #2 a gauge answer measured against a superseded capability is not applied, pinned by a test
- [ ] #3 SPEC 5.5 states the zero-window rule and which way its risk falls, at both sites: WindowFits and PrefixAwareScheduler.cs:85-86
- [ ] #4 a plan review at the tier this contract requires lands before any code, and this row's plan names the chosen shape
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
The decision comes first and it is the manager's to write: an immutable gauge tuple read alongside the capability snapshot, or the gauges folded into the snapshot itself — and what a stale tuple does, refuse the profile or keep treating unknown as unconstrained. No code before that plan is in this row.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The external commit review of ab54d27 supplies a counter-example this row has to settle: the profile-window check runs **after** `PublishProof`, so a switch refused for the window still publishes a transient positive proof and burns a capability version before the unknown publish. That is the same epoch confusion as the gauges — a snapshot that momentarily authorises a profile the engine cannot serve — so fold the ordering question into this row's design rather than opening another one. Note also that a refused switch bumps versions at start and end; SPEC's "a refusal burns no version" wording has to say which refusal and which publication.
<!-- SECTION:NOTES:END -->
