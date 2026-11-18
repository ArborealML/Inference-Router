---
id: TASK-68
title: >-
  Bind the serving gauges to a capability epoch, so a stale window cannot
  authorise a profile the engine cannot serve
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T01:11:00Z'
updated_date: '2026-11-18T04:27:00Z'
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
**Shape.** The capability snapshot already carries health and the model set behind a version switch claims compare-and-set against (v0.12.31). The three serving gauges — window, slots, inferred profile — are the leftover: plain volatiles written by `UpdateServingGauges`, read live by `WindowFits` and the escalation ladder. Put them in the snapshot, publish a gauge change through the existing fenced publish path so it is the only thing that bumps the version, and let `ReadyIn` answer from the snapshot it was handed.

**The sequence that must fail first.** A lane serves a 4k window; a 128k request claims a switch and records the version. Before its CAS a poll lands, the engine answers 8k for a model already loaded, and the poll writes the gauge. Today the version is untouched, the CAS wins, and the engine stops and reloads a window it already serves. After the fix the poll bumps the version, the CAS loses, the claim re-decides against the new snapshot, and the request becomes a dispatch or a named refusal.

**Settle before code.** (1) The gauge writer fires every poll even when nothing changed: publish on an actual change only, and keep unknown (0) writes out of the version. (2) A lost CAS re-decides rather than fails; bound the re-decides per request and name the refusal when they run out. (3) If the window moves mid-switch, settle-time proof reads the settle snapshot; say whether a widening gauge cancels a switch or it completes and re-reads, and why that cannot thrash. (4) Every remaining live read (`PrefixAwareScheduler.cs:76-92`) becomes a snapshot read or a stated gap in SPEC §15; TASK-76's negative publications follow the same rule. (5) Prefer a pin on the publish path — a claim against a bumped version loses — over a new production hook.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Two independent sources, one defect.** The commit review of `ab54d27..a228c6e` (external, 2026-11-18) named this as its top finding while this seat was reading the same code. Verified here: `UpdateServingGauges` writes `_activeContextWindow` from a probe result and never touches the capability version (`EngineRuntime.cs:489-500`), and `ReadyIn` answers from a snapshot except through `WindowFits`, which reads the live gauge (:590-610).

**Wake.** It wakes when the top-tier plan review of the plan below returns; that pass was dispatched 2026-11-18 and its verdict will be recorded here before any code.
<!-- SECTION:NOTES:END -->
