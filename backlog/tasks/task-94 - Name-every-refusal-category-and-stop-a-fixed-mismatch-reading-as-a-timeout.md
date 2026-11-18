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
- [x] #12 SPEC and the `RunSwitchAsync` comment state the invariant as implemented; the mutability claim narrows to the API.
- [x] #13 Per decision-49 the transaction resolves its catalog entry before the claim and reads no configuration of its own; the post-claim missing-entry branch goes.
- [ ] #14 The absent-entry refusal answers before a **new** claim with `no_select_command` and says absent, not unusable; a caller finding a matching claimed switch joins it first (decision-51), and §5.5 says so.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Round one closed at **2f7e3e6**; routed findings sit on TASK-103 #4, TASK-68 #6, TASK-104 #4. Counts on these commits are the coding seat's report.

**764a195** (v0.12.45) resolves the catalog entry before the claim, carries it into the switch, deletes the post-claim missing-entry branch and writes the mutation policy into §5.5 — #9, #11 and #13 ticked on reading that commit; the v0.12.44 diagnosis lives in the SPEC rows beside it.

Its review found that the moved guard refuses a caller that should have **joined** an in-flight switch, hence decision-51 and #14. Its ask to drop "or mark this engine remote" falls away: the remote route returns at `:686`, ahead of the guard, so the advice is actionable. Reads: one `RunSwitchAsync` call site (`:803`); no in-place write of a catalog entry in `src/`. decision-50 declines a private copy of the config fields, the graph being read-only after boot.
<!-- SECTION:NOTES:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Round four, one commit. #10: arm `AfterClaimForTests` on the stable blank pin and assert it never fires, the category, and the message halves. #14 (decision-51): resolve and validate the entry on the **claim** path inside the transition gate, so a caller that meets a matching claimed switch joins it instead — and keep the `Emit` outside the gate, because publishing under the switch lock is TASK-103 #4. Pin three shapes: first absence (both message halves), a joiner whose entry was removed mid-switch, and a joiner whose switch has already failed. Focused refusal and switch cases, clean build.
<!-- SECTION:PLAN:END -->
