---
id: TASK-76
title: >-
  Withdraw health only when the /models fetch failed, and never over a switch's
  own proof
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T02:36:00Z'
updated_date: '2026-11-18T20:22:00Z'
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
The commit review of 0e016e5 found the health loop's catch is wider than the observation it describes. Checked against the code: ProbeStateAsync swallows every fault and returns false, so it cannot withdraw health, and the only thrower left inside that try is the subscriber fan-out inside EventRing.Append, which today is one dispatch pulse (RouterCore.cs:63). So the rule that only a /models failure moves Healthy rests on a comment rather than on structure. The review's second finding is reachable as written: PublishProbeFailure carries no switch guard, so a poll that starts after a switch has published its proof and fails before the marker clears sets Healthy=false on the proven snapshot, and the clear then leaves a proved engine unknown for a whole poll interval.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A fault raised anywhere in the health round except the /models fetch cannot publish Healthy=false, pinned with an injected throwing seam
- [ ] #2 A poll that starts after a switch published its proof and then fails before the marker clears leaves the proven snapshot healthy
- [ ] #3 SPEC 5.5 names the one observation that moves Healthy, and both new pins fail before the change and pass after it, with the runs named
- [ ] #4 The failed-exit pin asserts the snapshot between the unknown publication and the marker clear, with an exact version check, so leaving the provenance bit set at the exit fails a pin.
- [ ] #5 A preflight refusal before mutation preserves the incumbent’s valid proof and permits a subsequent inference on it. An uncertain mutating operation withdraws readiness pending fresh proof.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Shape of the fix: narrow the try to the fetch, and publish the negative from that catch alone, so nothing else in the round can reach PublishProbeFailure. Give PublishProbeFailure a marker guard — while SwitchingToward is set the snapshot is already unknown except in the proof window, so suppressing there withdraws nothing but the switch proof. Pin with latches, not sleeps: the same review marked the existing health tests (IntervalMs=50 with 300 ms and 250 ms waits) as scheduling-dependent, and TASK-67 showed what a pin that tracks no event costs. This row goes first in the serving-gauge sequence: AC #2 is the marker guard on a probe negative, re-verified at EngineRuntime.cs:235-247, and the gauge row must not duplicate that half.

Two criteria came from the commit review of the provenance bit, both re-measured by this seat at d6cfb38 by reading, not running. AC #4: the exit pin asserts provenance only after the marker clear and its version test is at-or-above claimVersion+2, so a mutation that bumps the version but leaves the bit set at PublishUnknown passes — while a waiter reading between those two publications sees a claim-authored unknown and is answered Ready. AC #5: PublishProbeFailure early-returns on already-unknown health (line 247), so a refusal that mutated nothing leaves the engine unknown for a poll interval. The deeper attribution hole is not this row to close.

AC #4 target moved at 64b9f94: the last sub-case of the publication-class pin now waits on the gated launch (launchesBefore / Launches.Count greater than launchesBefore / SwitchingToward == "second") rather than reading the claim marker, which was a window that could close. Work from that shape.
<!-- SECTION:NOTES:END -->
