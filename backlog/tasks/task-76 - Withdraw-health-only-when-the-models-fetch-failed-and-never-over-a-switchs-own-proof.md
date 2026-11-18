---
id: TASK-76
title: >-
  Withdraw health only when the /models fetch failed, and never over a switch's
  own proof
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T02:36:00Z'
updated_date: '2026-11-18T20:50:00Z'
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
Pre-code review dispatched against 31179e3. The publisher already checks marker and version. The held-switch fixture starts an ordinary poll at the already-proven version and observes its failure before marker clear. Remaining work isolates actual models-fetch failure from faults elsewhere in the round and proves the exact provenance version at failed exit. Use bounded latches. TASK-77 stage B covers pre-mutation refusal preserving the incumbent for subsequent inference.

Plan approved for the combined lifecycle candidate. Prove pre-fetch failure preserves the captured snapshot, a failed ordinary poll cannot withdraw a marker-bearing healthy proof, and attempted mutation clears claim provenance one version before clearing the marker. Verify all three mutation controls on the integrated tree.
<!-- SECTION:NOTES:END -->
