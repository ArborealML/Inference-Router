---
id: TASK-61
title: >-
  Authorize a waiting caller at the moment it resumes, not at the moment it was
  promised
status: Done
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T23:07:00Z'
updated_date: '2026-11-18T19:29:00Z'
labels:
  - concurrency
  - scheduling
milestone: m-2
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
- [x] #7 A waiter's own proof survives a later claim but not a probe's failure: with a probe failure published between proof and claim the waiter is not answered Ready, and without it it is
- [x] #8 Claim provenance reads true only while the current claim authored the health withdrawal; every other health author clears it, and a provenance-only publication still advances the capability version
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Shipped at [d6cfb38](https://github.com/ArborealML/Inference-Router/commit/d6cfb38362ac64ead69d4db96216549043803126) (SPEC v0.12.38). One internal, non-positional bit on the capability snapshot: *the claim that raised the current marker authored the withdrawal, until another author supersedes it*. Set only when a claim starts from a healthy engine; cleared by a positive probe, a switch proof, a probe negative allowed to publish, a switch exit, or a marker clear. `PublishUnknown` no longer early-returns on already-false health: it publishes a provenance-only change and advances `Version`, because the scan reads `Version` as its "nothing moved" test. `ReadyIn` stays strict; only the own-proof branch reads the bit. [TASK-76](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-76%20-%20Withdraw-health-only-when-the-models-fetch-failed-and-never-over-a-switchs-own-proof.md) and [TASK-68](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-68%20-%20Bind-the-serving-gauges-to-a-capability-epoch-so-a-stale-window-cannot-authorise-a-profile-the-engine-cannot-serve.md) preserve this shape; they do not redecide it.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
AC #7 and #8 closed at [d6cfb38](https://github.com/ArborealML/Inference-Router/commit/d6cfb38362ac64ead69d4db96216549043803126), parent `e3e4f35`, and this seat confirmed the commit resolves, is `origin/main`, carries the implementation identity, and contains both pins by name — `AFailedProbeBetweenProofAndClaimWithdrawsTheWaitersOwnProof` in the switch-claim class and `TheClaimThatWithdrewHealthIsTheOnlyAuthorOfUnknownHealth` in the publication class. The pairing is the point: a marker alone still grants Ready to a waiter holding its own proof, and a probe failure between that proof and a later claim withdraws it. The probe is driven by the health loop, with the test seam removed. The coding seat reports 199 passed / 0 failed locally at that tree and two mutation probes each failing the intended pins; this seat did not run them. CI run 44272047987 was in flight when the row closed, so the gate is the last word, not the report. The accepted §15 gap — admission authority across the handoff, carried by [TASK-25](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-25%20-%20Reserve-the-switchs-own-ticket-before-a-lifecycle-transaction-drains-an-engine.md) AC #5, TASK-64 AC #3 and TASK-72 — stays open on those rows and is not this row's to close.

Gate closed: CI run 44272047987 on that commit completed green at 09:03Z, 194-plus tests, the shutdown pin not recurring in that run.

Commit review of e3e4f35..d6cfb38 landed: no blocker in the shipped bit — the reviewer found the CAS publication coherent and the bit absent from every wire body, and this seat confirmed both by reading the projection and the serializer. Two P2 follow-ons, each re-measured here by reading, are carried forward rather than reopening this row: [TASK-76](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-76%20-%20Withdraw-health-only-when-the-models-fetch-failed-and-never-over-a-switchs-own-proof.md) AC #4 and #5, and [TASK-96](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-96%20-%20Name-the-observation-that-withdrew-health-when-a-probe-answer-cannot-land-during-a-claim.md).
<!-- SECTION:NOTES:END -->
