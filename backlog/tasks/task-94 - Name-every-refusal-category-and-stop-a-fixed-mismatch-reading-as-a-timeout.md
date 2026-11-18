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
The taxonomy landed: every visible refusal carries one named category, the three readiness causes are separated, and §5.5 says what each means to the caller. The deep review of the follow-up commit reopened the row on three overclaims, each re-measured here. None changes an answer on the shipped boot path; one deletes a backstop, one is a false SPEC claim.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every refusal a caller can see carries one named category, and a test walks every exit rather than a selection.
- [x] #2 A permanent configuration mismatch and a transient timeout do not share one category name, and SPEC states the caller's action for each.
- [x] #3 SPEC 5.5 says what each category means to the caller — retry, retry elsewhere, or fix the configuration.
- [x] #4 The pin states the expected literals itself, asserts set equality against `SwitchReason`, and the audit accounts for every failed return.
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The added switch event is emitted once, before waiters settle, with cleanup in `finally`, so a feed observer cannot wedge the claim marker
- [x] #7 The deep commit review of 14a01e2 lands and each finding carries a disposition: fixed later, or written up as an accepted SPEC gap
- [ ] #8 The pre-claim selector check and `RunSwitchAsync` resolve one catalog definition (decision-48), so a same-name divergent definition cannot make the two sides disagree.
- [ ] #9 SPEC states the mutation policy that follows — an edit after that read changes no claimed transaction — and a test pins it.
- [ ] #10 A test using `AfterClaimForTests` shows a stable blank-selector entry never claims: no witness fire, `no_select_command`, last proof intact.
- [ ] #11 A test with two same-name definitions whose catalogs disagree pins what a divergent configuration answers, in both directions.
- [ ] #12 SPEC 5.5, the v0.12.43 row and the comment in `RunSwitchAsync` describe the invariant as implemented; the closed-set mutability claim narrows to the advertised API.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Delivered at **14a01e2** (SPEC v0.12.42), corrected at **2f7e3e6** (v0.12.43); CI green at both; counts are the coding seat's report. That round's seven findings are closed on the SPEC arm, the span, the whole-answer pins and the pre-claim refusal; routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4.

Anchors are read at `32cd72e`; line numbers move with in-flight edits, so they are named as well as numbered: the pre-claim check reads `_def.Catalog` (`EngineRuntime.cs:688`), `RunSwitchAsync` re-reads `_config.Engines[_def.Name]` (`:1011`), and the seam `BeforeSwitchWaitForTests` runs between them (`:712`), while the comment at `:1026` asserts the deleted branch unreachable. Reach it and the router launches, fails, and publishes the engine's proof unknown — the loss the precheck exists to prevent. The blank-selector pin aliases one definition into both sources and asserts only `SwitchingToward == null`, which a post-claim failure also leaves null.

The review of this row's own commit asked for the mutation semantics to be chosen, not left open, and for the documentation fixes to split: hence decision-48 and criteria #8–#12. Shutdown-race work stays on TASK-87.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
One commit, per decision-48: resolve one `EngineModelDefinition` before the claim marker goes up and pass that object through the switch path rather than re-adding the in-switch ask; pin #10 and #11; write the mutation policy into SPEC and correct §5.5, the v0.12.43 row and the `RunSwitchAsync` comment. Build clean plus the focused refusal and switch cases; a full run belongs to whichever round crosses engine lifecycle.
<!-- SECTION:PLAN:END -->
