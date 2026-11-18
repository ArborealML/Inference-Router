---
id: TASK-94
title: Name every refusal category, and stop a fixed mismatch reading as a timeout
status: To Do
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
The taxonomy landed: every visible refusal carries one named category, the three readiness causes are separated, and §5.5 says what each means to the caller. The deep review of the follow-up commit reopened the row on three overclaims, each re-measured here. None changes a caller-visible answer on the shipped boot path; one deletes a real backstop and one is a false SPEC claim.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every refusal a caller can see carries one named category: the two free-form exits join the set or the set narrows to them, and a test walks every exit rather than a selection.
- [x] #2 A permanent configuration mismatch and a transient timeout are not called by one category name, or SPEC states the phase-only reading in one sentence and the caller's retry decision follows from it.
- [x] #3 SPEC 5.5 says what each category means to the caller — retry, retry elsewhere, or fix the configuration.
- [x] #4 The pin states the expected literals itself, asserts set equality against `SwitchReason`, and an audit accounts for every failed return so a tenth exit cannot pass silently
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The added switch event is emitted once, before waiters settle, with cleanup in `finally`, so a feed observer cannot wedge the claim marker
- [x] #7 The deep commit review of 14a01e2 lands and each finding carries a disposition: fixed in a later commit, or written up as an accepted SPEC gap
- [ ] #8 The pre-claim selector check and `RunSwitchAsync` consult one catalog definition — restore the post-lookup backstop, or resolve one definition before the claim and pass that snapshot through — so a divergent or mutated definition cannot skip the refusal.
- [ ] #9 One `AfterClaimForTests` witness that the stable blank-selector case never claims, plus a `BeforeSwitchWaitForTests` regression flipping a usable entry to blank after the check, under both lifecycle providers.
- [ ] #10 SPEC stops calling the in-switch selector branch unreachable, and the uneditable-set claim narrows to what the advertised API allows.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Delivered at **14a01e2** (SPEC v0.12.42), corrected at **2f7e3e6** (v0.12.43); CI green at both. Counts there are the coding seat's report. The v0.12.42 review's seven findings are dispositioned and closed: the false §5.5 stop arm replaced (cancellation / 499, naming `RouterCore`'s `OperationCanceledException` arm), `SwitchReason.All` moved to `ReadOnlySpan<string>`, the refusal walk asserts `Detail` whole, the blank-`select.exec` case pinned before the claim; routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4.

Re-measured here for the v0.12.43 review: the pre-claim check reads `_def.Catalog` (`EngineRuntime.cs:709`) while `RunSwitchAsync` reads `_config.Engines[_def.Name]` (`:1046`), so the deleted in-switch guard was reachable — a same-name divergent definition, or an entry replaced after the check (`BeforeSwitchWaitForTests`, `:733`) — and reaching it yields `launch_failed` with the engine's proof discarded, the failure the precheck exists to prevent. The blank-selector pin installs one definition object in both sources and asserts only `SwitchingToward == null`, so it witnesses neither shape. SPEC v0.12.43 and §5.5 both assert unreachability; #10 corrects that.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One commit at the implementer's prefix: give the switch path one definition source — a snapshot captured before the claim is preferred over re-adding the branch, since the branch alone leaves two sources — add the two pins in #9, and correct the SPEC v0.12.43 row plus the §5.5 sentence. Build clean plus the focused refusal and switch cases; a full run belongs to whichever round crosses engine lifecycle. Shutdown-race work stays on TASK-87.
<!-- SECTION:PLAN:END -->
