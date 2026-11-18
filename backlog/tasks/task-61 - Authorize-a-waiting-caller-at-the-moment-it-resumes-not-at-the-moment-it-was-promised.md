---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T06:53:00Z'
labels:
  - concurrency
  - scheduling
dependencies:
  - TASK-25
  - TASK-72
priority: high
ordinal: 61000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner's posture: a switch the engine cannot confirm is **refused** (decision-27). The invariant: **a waiting caller is told Ready only if the capability snapshot still authorises it when it resumes.** The first attempt ping-ponged (decision-36); this row is the narrowed half plus the gap it leaves.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The cannot-confirm refusal landed first in its own commit; this row changes only how a resuming waiter is classified
- [x] #2 A switch whose own attempt ended false ends Failed once, so a caller with no ceiling cannot relaunch forever
- [x] #3 A waiter whose own attempt succeeded takes its own turn rather than a refusal when something other than a later claim contradicts the target
- [x] #4 The resume check is pinned by a gate between the readiness await and the snapshot read, so the pin cannot pass on thread scheduling
- [x] #5 A marker raised by a LATER claim never withdraws the proof a waiter's own switch published: that caller is not sent behind another caller's transaction
- [x] #6 Two callers wanting different models on a one-model engine produce no alternating launch sequence — pinned as a launch sequence, not as the absence of a timeout
- [ ] #7 A waiter's own proof survives a later claim but not a probe's failure: with a probe failure published between proof and claim the waiter is not answered Ready, and without it it is
- [ ] #8 Claim provenance reads true only while the current claim authored the health withdrawal; every other health author clears it, and a provenance-only publication still advances the capability version
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Commits 1 (`f89fead`) and 2 (`6ea83ca`) landed: the four resume answers, then AC #6's sequence pin with arrivals counted at `BeforeSwitchWaitForTests`.

**3, AC #7-#8 — design settled by the plan review, re-checked at `6ea83ca`.** One internal, non-positional `EngineCapability` member meaning "the claim that raised the current marker changed `Healthy` from true to false, and no later non-claim author superseded it". Rules: true only at claim start when health was true; false at a positive probe, a switch proof, an allowed probe negative, a marker clear, and at `PublishUnknown` (`:280`) — which returns early when health is already false, so a provenance-only change must still bump `Version`. `ReadyIn` (`:590-598`) stays strict; only the own-proof branch reads the bit. Pinned as a pair beside `SwitchClaimTests.cs:397` on the existing seams: a probe failure published between proof and claim must deny Ready, and the current pin must still grant it. Mutation control: the predicate arm alone. No new member on `EngineStats` (decision-41). Then TASK-76, then TASK-68 carries the bit without redeciding it.

The resume check is not the entitlement: authority across the handoff is TASK-25's criterion.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Green through `6ea83ca` (v0.12.36)**, run 44250245947 success. AC #3-#6 verified at HEAD: `OwnProofStillStands` (`EngineRuntime.cs:615-618`) and its pin (`SwitchClaimTests.cs:397`), the sequence pin (`:454`, `extra, m` twice), both pins on the seam.

**Both reviews ingested; every claim re-checked in code.** F1 is this row's, now AC #7-#8: one bit cannot say who withdrew health — `PublishProbeFailure` (`:235-245`) and `TryPublishSwitchStarted` (`:302`) publish the same `Healthy = false`. A claim preserving the health it displaced was rejected: `PublishUnknown` (`:280`) returns early when health is already false, so stale provenance survives it. F3 (unprotected handoff after a Ready answer) stays parked in SPEC 15, proof check on TASK-25; F2 (unversioned gauge) and F4 (re-decide under two locks) are TASK-68's. `EngineCapability` reaches no wire surface (`AirServerJson.cs:11-16`; decision-41). Do not revert `042e212`; TASK-62's category work follows this row.
<!-- SECTION:NOTES:END -->
