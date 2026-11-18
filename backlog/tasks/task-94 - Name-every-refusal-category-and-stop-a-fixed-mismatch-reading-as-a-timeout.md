---
id: TASK-94
title: Name every refusal category, and stop a fixed mismatch reading as a timeout
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T08:30:00Z'
labels:
  - wire
  - switch
dependencies: []
priority: medium
ordinal: 93000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Re-measured at 610f0d8 after the commit review. Two refusal exits still hand the caller free-form prose outside the closed set of seven: EngineRuntime.cs:620, where a remote engine loads its own models, and :728-729, where the switch task never started — the second interpolates the switch target into the caller's sentence. Separately, one token carries three different causes: with the prove-health path on, an unhealthy probe, a /models answer that omits the target, and a reported window too small all arrive as the same category, while with it off the latter two get their own names. A caller cannot tell a fixed configuration mismatch from a transient timeout, which is the one distinction a retry decision needs. This is the taxonomy half of the refusal work; the size and leak half is the row before it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every refusal a caller can see carries one named category: the two free-form exits join the set or the set narrows to them, and a test walks every exit rather than a selection.
- [ ] #2 A permanent configuration mismatch and a transient timeout are not called by one category name, or SPEC states the phase-only reading in one sentence and the caller's retry decision follows from it.
- [ ] #3 SPEC 5.5 says what each category means to the caller — retry, retry elsewhere, or fix the configuration.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The rule about new pins lives on TASK-87 and covers the switch-claim and capability-publication classes. It is not a checker rule, and this seat's earlier wording on another row named a checker rule that does not exist. The amendment in `SwitchClaimTests` replaces one assertion in an existing test — no new pin, wait, latch or method — so it is an amendment inside the rule and is allowed. Conditions: minimum edit, no new waits, and every red run names its failing test while TASK-87 is open.
Held: code and tests uncommitted, SPEC draft parked and renumbered v0.12.42 so the shipped v0.12.41 does not collide. Wake: the top-tier plan review of this design landing (requested 11:22Z); on landing the seat re-applies the SPEC draft, re-runs focused and full Release, then commits.
<!-- SECTION:NOTES:END -->
