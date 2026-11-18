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
- [x] #2 A permanent mismatch and a transient timeout do not share one name, and SPEC states the caller's action.
- [x] #3 SPEC 5.5 says what each category means: retry, retry elsewhere, fix the configuration.
- [x] #4 The pin states its literals, asserts set equality against `SwitchReason`, and the audit covers every failed return.
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The switch event is emitted once, before waiters settle, with `finally` cleanup, so a feed observer cannot wedge the claim
- [x] #7 Each 14a01e2 finding carries a disposition: fixed later, or an accepted SPEC gap
- [x] #8 One catalog definition feeds the check and `RunSwitchAsync` (decision-48), so a divergent same-name definition cannot split the answer.
- [x] #9 SPEC states the mutation policy: an edit after that read changes no claimed transaction. A test pins it.
- [ ] #10 A test with `AfterClaimForTests` shows a stable blank entry never claims: no claim, `no_select_command`, proof kept, provider not asked.
- [x] #11 The divergent pin runs both directions and asserts the provider kind, so a case that never reaches the switch cannot pass.
- [x] #12 SPEC and the `RunSwitchAsync` comment state the invariant as implemented; the mutability claim narrows to the advertised API.
- [x] #13 Per decision-49 the transaction resolves its catalog entry before the claim and reads no configuration of its own; the post-claim missing-entry branch goes.
- [ ] #14 The absent-entry refusal answers before the claim with `no_select_command` and says absent, not unusable; a caller joining a claimed switch is not refused.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round one closed at **2f7e3e6** (SPEC v0.12.43 carries the detail); routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4. Counts are the coding seat's report.

Read at `32cd72e`: the check read `_def.Catalog` (`:688`) while `RunSwitchAsync` re-read `_config.Engines[_def.Name]` (`:1011`), with `BeforeSwitchWaitForTests` between them (`:712`); reaching it launched, failed and published the proof unknown. The old pin aliased one definition into both sources and asserted only `SwitchingToward == null`.

Rounds two and three: **0dafcd2** (v0.12.44) unified the definition but left the **entry** lookup split — both sides queried `def.Catalog` at different times (`:695`, `:1021`). **764a195** (v0.12.45) resolves the entry before the claim, carries it into the switch, deletes the post-claim missing-entry branch and writes the mutation policy into §5.5 — #9, #11, #13 ticked on reading that commit; its CI run was superseded by a later push, so the gate is read on the head. Still open: #10, because the stable blank pin asserts `LaunchCalls == 0` but never instruments `AfterClaimForTests`; and #14, where the wording landed and the joined-caller case is unpinned. decision-50 declines a private copy of the config fields (the graph is read-only after boot); the record review's ask to split #12 is declined as one SPEC change.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two assertions, one small commit: instrument `AfterClaimForTests` in `ABlankSelectorExecIsRefusedBeforeTheClaimToo` (#10), and pin that a caller arriving while a switch is claimed still joins rather than being refused by the moved check (#14). Nothing else is open here. A full Release run belongs to the round that next crosses engine lifecycle.
<!-- SECTION:PLAN:END -->
