---
id: TASK-76
title: >-
  Withdraw health only when the /models fetch failed, and never over a switch's
  own proof
status: Done
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-18T02:36:00Z'
updated_date: '2026-11-20T21:52:00Z'
labels:
  - concurrency
milestone: m-2
dependencies:
  - TASK-67
priority: medium
ordinal: 76000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
After a read-only preflight refusal, a fresh inference completed on the modeled incumbent without another probe, switch or daemon write. The [landed fixture](https://github.com/ArborealML/Inference-Router/pull/59) passed, failed at the dispatch assertion when restoration was disabled, then passed again after restoration. All three [post-merge checks](https://github.com/ArborealML/Inference-Router/actions/runs/44823339917) passed on 3a8505a. Existing controls cover readiness withdrawal after an uncertain mutation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 A fault raised anywhere in the health round except the /models fetch cannot publish Healthy=false, pinned with an injected throwing seam
- [x] #2 A poll that starts after a switch published its proof and then fails before the marker clears leaves the proven snapshot healthy
- [x] #3 SPEC §5.5 identifies the awaited /models fetch as the ordinary health observation. Named tests reproduce the pre-fetch baseline failure and detect reversal of the existing marker guard. Both pass with the protections in place.
- [x] #4 The failed-exit pin asserts the snapshot between the unknown publication and the marker clear, with an exact version check, so leaving the provenance bit set at the exit fails a pin.
- [x] #5 A preflight refusal before mutation preserves the incumbent’s valid proof and permits a subsequent inference on it. An uncertain mutating operation withdraws readiness pending fresh proof.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Extend the existing native preflight refusal test. Capture the incumbent proof, refuse the competing target, then dispatch one fresh completion to the incumbent. Assert the exact application POST and response with no new probe, switch or daemon write. Preserve the additional exact-inspect reply when composing with the pending observation change. Run this method, reverse the native restoration decision to demonstrate the intended failure, then restore it and rerun. Use the landed uncertain-withdrawal controls for the other outcome.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [combined implementation](https://github.com/ArborealML/Inference-Router/commit/fcff3ea) passed 90 integrated cases, 421 full Release tests and server Native AOT. A broad exception filter initially missed a late HTTP 503 when the polling loop was cancelled. The corrected code preserves that evidence. APreFetchFaultCannotWithdrawHealth failed on 87d7043 with the injection seam and again when the broad catch was restored on 80f3bda. FailedPollDuringASwitchCannotWithdrawTheSwitchsPublishedWindows failed when the existing marker guard was deliberately reversed. Each named test passed 1/1 after restoration, with zero skips. The separate intermediate-provenance mutation covers criterion 4. Docker lifecycle observations have their own rules in §5.3.1. Returned preflight refusals and attempted mutations passed; thrown read-only operations and subscriber failures remain with TASK-77.

Review of 80f3bda found remaining coverage gaps. Hold a same-generation models request across loop cancellation and distinguish a 503, the independent fetch timeout, and cancellation attributed to the loop. After a successful fetch, inject a diagnostic fault and verify the published snapshot and next tick. Pin thrown mutation and readiness-timeout exits. Align §5.5 with PublishUnknown when health was already unknown without claim provenance: that path skips the intermediate version increment.
<!-- SECTION:NOTES:END -->
