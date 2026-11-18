---
id: TASK-105
title: Stop a switch authorising a profile on absent or stale window evidence
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T13:17:00Z'
labels: []
dependencies: []
ordinal: 104000
---

**Archived by the board as a duplicate.** Its finding — an unknown or stale serving window authorising a profile — is carried by TASK-68 criterion #6, the row whose admission check it describes; the survivor keeps the criteria the board reads.

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The switch asks whether the engine's serving window fits the profile before it starts, and the answer is a comparison against a gauge. Two states make that comparison meaningless, and both currently pass it: a window of zero — the value the code uses for "never observed" — satisfies the check for **every** profile, and a window learned from an earlier probe stays authoritative after a later probe stops reporting one. So a router can declare a window too small for an engine it has never measured, or serve a profile it can no longer show fits. The deep commit review of 14a01e2 found it; this seat re-measured both states in the code. It is not TASK-68's fix: keying the gauge to a capability epoch decides *whose* measurement a number is, not whether an absent measurement authorises anything.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria

<!-- AC:BEGIN -->
- [ ] #1 An unknown serving window authorises no profile: the switch refuses, and the refusal is a named cause that is not `window_too_small`
- [ ] #2 When a probe stops reporting a window, the previous value stops counting as current evidence, and the switch re-decides rather than carrying it forward
- [ ] #3 Pins for both states, each shown red before the fix and green after, with a mutation control that leaves the known-window pin green
- [ ] #4 SPEC §5.5 and §7.6 say what an unmeasured window means for admission, and §15 stops treating unreported-window as a policy question
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Measured by this seat at `eba81d4`: `WindowFits` at `EngineRuntime.cs:651-656` returns true for `window == 0` (the doc-comment says "gauge 0 = unknown → never block on it", so today the unknown side is a pass); the reviewer's second half — a stale positive surviving an omitting probe — shares the mechanism TASK-68 describes at `ChatCompletionsProbe.cs:136`. Admission-path behaviour, not the wire: plan review at the deep tier, not top. Sequence after TASK-68, whose epoch keying decides which measurement a gauge belongs to; this row decides what no measurement may do.
<!-- SECTION:NOTES:END -->
