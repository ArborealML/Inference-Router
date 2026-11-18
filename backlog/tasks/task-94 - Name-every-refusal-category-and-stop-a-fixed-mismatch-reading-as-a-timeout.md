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
Two review rounds reopened this taxonomy row: the follow-up commit overclaimed three times, and the fix that answered them still leaves one lookup split. None changes an answer on the shipped boot path; all is reachable through the public constructor and the test seams.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every visible refusal carries one named category, and a test walks every exit.
- [x] #2 A permanent configuration mismatch and a transient timeout do not share one category name, and SPEC states the caller's action for each.
- [x] #3 SPEC 5.5 says what each category means to the caller: retry, retry elsewhere, or fix the configuration.
- [x] #4 The pin states its expected literals, asserts set equality against `SwitchReason`, and the audit covers every failed return.
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The added switch event is emitted once, before waiters settle, with cleanup in `finally`, so a feed observer cannot wedge the claim marker
- [x] #7 The deep commit review of 14a01e2 lands with a disposition per finding: fixed later, or an accepted SPEC gap
- [x] #8 The pre-claim selector check and `RunSwitchAsync` resolve one catalog definition (decision-48), so a same-name divergent definition cannot make the two sides disagree.
- [ ] #9 SPEC states the mutation policy — an edit after that read changes no claimed transaction — and a test pins it.
- [ ] #10 A test using `AfterClaimForTests` shows a stable blank-selector entry never claims: no witness fire, `no_select_command`, last proof intact.
- [ ] #11 A test with two same-name definitions whose catalogs disagree pins the answer in both directions.
- [x] #12 SPEC 5.5, the v0.12.43 row and the `RunSwitchAsync` comment state the implemented invariant; the mutability claim narrows to the advertised API.
- [ ] #13 Per decision-49 the transaction also resolves its catalog entry before the claim, doing no catalog read of its own; the absent-entry refusal joins the pre-claim answer.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round one closed at **2f7e3e6** (v0.12.43) after **14a01e2**: the false §5.5 arm, the span, whole-answer pins, the pre-claim refusal; routed findings on TASK-103 #4, TASK-68 #6, TASK-104 #4. Counts there are the coding seat's report.

Read at `32cd72e`: the pre-claim check read `_def.Catalog` (`:688`) while `RunSwitchAsync` re-read `_config.Engines[_def.Name]` (`:1011`), with `BeforeSwitchWaitForTests` between them (`:712`) a comment at `:1026` called the branch unreachable. Reach it and the router launches, fails, and publishes the engine's proof unknown — the loss the precheck exists to prevent. The old pin aliased one definition into both sources, and asserted only `SwitchingToward == null`.

Round two: **0dafcd2** (v0.12.44) resolves one definition at the claim site (`:685`) and passes it to the switch (`:792`), pinned under both providers; #8 and #12 ticked on reading that commit. It leaves the **entry** lookup split — both sides query `def.Catalog` at different times (`:695`, `:1021`) — so decision-49 extends the snapshot to the entry, moving the absent-entry refusal in front of the claim and retiring a branch (#13). The record review asked to split #12's two documentation outcomes; declined, they are one SPEC change.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Round three, per decision-49: resolve the target's `EngineModelDefinition` before the claim alongside the engine definition, pass both down, and let the post-claim missing-entry branch go rather than re-adding a backstop. Then #9's SPEC sentence and pin, #10's witness on the stable case, #11 in both directions. Focused refusal and switch cases plus a clean build; a full run belongs to the round that crosses engine lifecycle.

<!-- SECTION:PLAN:END -->
