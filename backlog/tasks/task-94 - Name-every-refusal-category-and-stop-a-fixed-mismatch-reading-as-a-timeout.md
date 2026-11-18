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
- [ ] #10 A test arms `AfterClaimForTests` on a stable blank entry: no claim, `no_select_command`, the published capability snapshot unchanged, provider not asked.
- [x] #11 The divergent pin runs both directions and asserts the provider kind, so a case that never reaches the switch cannot pass.
- [x] #12 SPEC and the `RunSwitchAsync` comment state the invariant as implemented; the mutability claim narrows to the API.
- [x] #13 Per decision-49 the claim resolves the entry once and the switch consumes it without a second lookup; the post-claim missing-entry branch is gone.
- [ ] #14 The absent-entry refusal answers a **new** claim with `no_select_command`, saying absent not unusable; a caller finding a matching claimed switch joins it first (decision-51), and §5.5 says so.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round one closed at **2f7e3e6**; its routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4. Counts are the coding seat's report unless read here.

**0ddd8f3** (v0.12.46, tests) re-closes #9: it asserts the selector handed to the provider (`LaunchedExec`, `:572`) against an entry swapped to blank, and the reported control — re-read the live config — reddens that pin alone. The earlier `false ||` control reddened nothing; decision-52 is what it taught.

Its review met two asks my excerpt omitted: `Expect` (`:608`) asserts the whole caller-facing `Detail`, so the `DoesNotContain("exec", …)` belt is redundant, not a boundary; and `Healthy` alone does not pin proof kept — a replaced snapshot passes it. #10 is open on that half, naming the assertion. #14 remains; plan reviewed and accepted.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Round four, one commit. Under the transition gate: read the switch state; a matching active switch is **joined** and its `Ready` awaited with no catalog read; otherwise the capability check, then resolve and validate the entry once for that attempt; invalid is a terminal refusal — zero publishes, zero state writes, counted through seams rather than inferred from a missing `switch_started` — and valid claims and carries that entry down. One outcome is selected under the gate and acted on after release; one absolute deadline spans wait, join and retry. No sequencer: feed ordering is promised within a transaction, not across callers. Red-first pins: a joiner whose entry is removed mid-switch; a joiner whose switch then fails (refusal after, one `switch_started`, one witness). #10's hook and the absence wording are regression-only.
<!-- SECTION:PLAN:END -->
