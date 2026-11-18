---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: In Progress
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T06:32:00Z'
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
The owner's posture: a switch the engine cannot confirm is **refused** (decision-27). The invariant: **a caller waiting on a switch is told Ready only if the capability snapshot still authorises it when it resumes.** The first attempt ping-ponged (decision-36); this row is the narrowed half plus the gap it leaves.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The cannot-confirm refusal landed first in its own commit; this row changes only how a resuming waiter is classified
- [x] #2 A switch whose own attempt ended false ends Failed once, so a caller with no ceiling cannot relaunch forever
- [x] #3 A waiter whose own attempt succeeded takes its own turn rather than a refusal when something other than a later claim contradicts the target, pinned where a second claim wins the engine
- [x] #4 The resume check is pinned by a gate between the readiness await and the snapshot read, so the pin cannot pass on thread scheduling
- [x] #5 A switch marker raised by a LATER claim never withdraws the proof a waiter's own switch published: a caller that holds its own proof is not sent behind another caller's transaction
- [x] #6 Two callers wanting different models on a one-model engine produce no alternating launch sequence — pinned as a launch sequence, not as the absence of a timeout
- [ ] #7 Health absence is attributed to the publisher that caused it: a probe-withdrawn health contradicts this waiter's own proof, so a marker alone cannot license a Ready answer — pinned with a probe failure published between proof and claim.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Three commits. **1, landed `f89fead`:** SPEC 5.5 as four resume answers, `OwnProofStillStands`, both resume pins on the seam. **2, landed `6ea83ca`:** AC #6's sequence pin; the fairness class counts arrivals at `BeforeSwitchWaitForTests` instead of sleeping. **3, AC #7:** attribute a health absence to the publisher that caused it (decision-40), so a probe-withdrawn health contradicts this waiter's own proof; pinned by publishing a failed probe between the proof and the claim, gating the claim, releasing the waiter at the seam and requiring that it not answer Ready, while the no-probe case still does. The 5.5 sentence and AC #3's wording move with it. **That design goes to a top-tier plan review before any code exists** (decision-23: concurrency plus lifecycle).

Do not read the resume check as the entitlement: authority held across the handoff is TASK-25's criterion.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**Green through `6ea83ca` (v0.12.36):** run 44250245947 success. AC #3-#6 verified at HEAD: `OwnProofStillStands` (`EngineRuntime.cs:615-618`), the own-proof pin (`SwitchClaimTests.cs:397`), the sequence pin (`:454`, `extra, m` twice, `GateCount` 0), both resume pins on the seam, no sleeping arrival left in fairness. Repeated-run and mutation counts are theirs.

**Deep review of the two fix commits landed; one accepted finding sits in this row's own read.** F1, now AC #7: `ReadyIn` and `OwnProofStillStands` reason about absent health from one bit, so a probe withdrawal published before the claim (`PublishProbeFailure` `:235-245`, unfenced — that half is TASK-76) is indistinguishable from the claim's own `Healthy = false` (`:302`), and a waiter answers Ready on health nobody confirmed after a failure. Rule: decision-40. **AC #7's design is at a top-tier plan review: no `EngineCapability` change before it lands.** F3, the unprotected handoff between a Ready answer and dispatch, stays parked in SPEC 15 with its named owners; the review's proof check is on TASK-25. F2 (the unversioned gauge this read shares) and F4 (the re-decide loop under the switch lock) are TASK-68's. Standing rules: do not revert `042e212`; TASK-62's category work comes after this row is Done.
<!-- SECTION:NOTES:END -->
