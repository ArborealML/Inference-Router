---
id: TASK-94
title: Name every refusal category, and stop a fixed mismatch reading as a timeout
status: Done
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
Review rounds reopened this row: a follow-up commit overclaimed, and its fix moved a guard that now refuses a caller that should join.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every visible refusal carries one named category, and a test walks every exit.
- [x] #2 A permanent mismatch and a transient timeout do not share one name, and SPEC gives the caller's action.
- [x] #3 SPEC 5.5 says what each category means: retry, retry elsewhere, fix the configuration.
- [x] #4 The pin states its literals, asserts set equality against `SwitchReason`, and the audit covers every failed return.
- [x] #5 The wait-ceiling path (`RouterCore.cs:551-554`) joins the named set, or SPEC scopes the set to completed attempts
- [x] #6 The switch event is emitted once, before waiters settle, with `finally` cleanup, so a feed observer cannot wedge the claim
- [x] #7 Each 14a01e2 finding carries a disposition: fixed later, or an accepted SPEC gap
- [x] #8 The check and the switch consume the same resolved catalog entry (decision-48), not two lookups.
- [x] #9 SPEC says a post-boot edit changes no already-claimed transaction; the pin names the selector handed to the provider, old and new distinguishable.
- [x] #10 A test arms `AfterClaimForTests` on a stable blank entry: no claim, `no_select_command`, the published capability snapshot unchanged, provider not asked.
- [x] #11 The divergent pin runs both directions and asserts the provider kind, so a case that never reaches the switch cannot pass.
- [x] #12 SPEC and the `RunSwitchAsync` comment state the invariant as implemented; the mutability claim narrows to the API.
- [x] #13 Per decision-49 the claim resolves the entry once and the switch consumes it without a second lookup; the post-claim missing-entry branch is gone.
- [x] #14 The absent-entry refusal answers a **new** claim with `no_select_command`, saying absent not unusable; a caller finding a matching claimed switch joins it first (decision-51), and §5.5 says so.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round one closed at **2f7e3e6**; routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4. Counts below are the coding seat's report unless read here.

**#10** — `ABlankSelectorExecIsRefusedBeforeTheClaimToo` captures `before = engine.Capability` and asserts the same `Version` and the same loaded list afterwards (`RefusalCategoryTests` at `d9b61f1`); the reported control — bump the published version at the refusal arm — reddens that pin alone at the version assertion, which is the half the earlier pin lacked.

**#14** — both catalog guards left the head of `EnsureLoadedAsync` and are now asked under `_transitionGate` only of a caller the idle engine hands to the claim path; the refusal's `engine.switch_failed` is emitted after the switch lock is released, and the deadline is set once before the first lock wait. Four pins in a new class, `SwitchJoinTests`, all four reported red at `d9b61f1` and green at `51922fe`, so no new pin joined the two classes TASK-87 is measuring. §5.5 states the order; decision-51 is the rule it rests on.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Closed at `51922fe` (SPEC v0.12.48) with the #10 half at `d9b61f1` (v0.12.47), both read at those shas. Join a matching in-flight switch before any catalog read; capability check, then resolve and validate the entry once per acquisition attempt (`ResolveClaimEntry`, `EntryGuard`); invalid is terminal and counted through seams as zero claims, zero publishes, zero state writes; valid claims and carries that entry. One outcome selected under the gate, spoken after the switch lock is released; one absolute deadline across wait, join and retry. No feed sequencer — ordering is promised inside one transaction, not across callers.
<!-- SECTION:PLAN:END -->
