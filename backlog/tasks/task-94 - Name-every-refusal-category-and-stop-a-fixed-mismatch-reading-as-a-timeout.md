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
Re-measured at 610f0d8. Two refusal exits still hand the caller free-form prose outside the closed set of seven: EngineRuntime.cs:620, where a remote engine loads its own models, and :728-729, where the switch task never started — the second interpolates the switch target into the caller's sentence. Separately, one token carries three causes: with the prove-health path on, an unhealthy probe, a `/models` answer omitting the target, and a window too small all arrive as one category; with it off, the last two get their own names. A caller cannot tell a fixed configuration mismatch from a transient timeout, which is the one distinction a retry decision needs. This is the taxonomy half of the refusal work; the size and leak half is the row before it.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every refusal a caller can see carries one named category: the two free-form exits join the set or the set narrows to them, and a test walks every exit rather than a selection.
- [ ] #2 A permanent configuration mismatch and a transient timeout are not called by one category name, or SPEC states the phase-only reading in one sentence and the caller's retry decision follows from it.
- [ ] #3 SPEC 5.5 says what each category means to the caller — retry, retry elsewhere, or fix the configuration.
- [ ] #7 The pin states the expected literals itself, asserts set equality against `SwitchReason`, and an audit accounts for every failed return so a tenth exit cannot pass silently
- [ ] #8 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [ ] #9 The added switch event is emitted once, before waiters settle, with cleanup in `finally`, so a feed observer cannot wedge the claim marker
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The rule about new pins lives on TASK-87 and covers the switch-claim and capability-publication classes. It is not a checker rule, and this seat's earlier wording on another row named a checker rule that does not exist. The amendment in `SwitchClaimTests` replaces one assertion in an existing test — no new pin, wait, latch or method — so it is an amendment inside the rule and is allowed. Conditions: minimum edit, no new waits, and every red run names its failing test while TASK-87 is open.
Held: code and tests uncommitted, SPEC draft parked and renumbered v0.12.42 so the shipped v0.12.41 does not collide. Wake: the top-tier plan review of this design landing (requested 11:22Z); on landing the seat re-applies the SPEC draft, re-runs focused and full Release, then commits.
Top-tier plan review: REVISE, **not** gated behind TASK-87 — the standing rule bars new pins in `SwitchClaimTests`/`CapabilityPublicationTests`, not amendments or a new class. Dispositions, verified where I could: one owner for the set, `SwitchReason` (`WireText.cs:92`; seven at HEAD, nine with the held patch); SPEC §5.5 is the normative action table, and §6.1 presenting feed phrases as caller tokens is a defect this commit fixes; the wire `code` stays `switch_failed` at 503 and only the message changes; `health_timeout` conflating a permanent mismatch with a transient probe miss is criterion 2; `remote_engine` is contested, because TASK-77 separates remote reachability from lifecycle ownership — decide the name and say why in SPEC. Two required mutations, each reddening the new pin: drop a token, answer one exit with prose. The feed hazard is confirmed here: `RouterEvent.cs:85` calls every subscriber inside the append with no isolation, so an observer's exception reaches the emitter. Guard at the emit site is criterion 9; the general fix is TASK-103.
<!-- SECTION:NOTES:END -->
