---
id: TASK-44
title: Close the open release-gate review findings (SPEC v0.12.15 onward)
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-17T10:52:00Z'
updated_date: '2026-11-18T21:25:00Z'
labels:
  - ci
  - release
  - security
milestone: m-9
dependencies: []
priority: high
type: task
ordinal: 43000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Qualify the release diagnostic fixtures. Commit 806d10d fixed the summary truncation and field-type rendering before the canary at 9910c0f. One regression fixture passes on the earlier workflow. Identify a reachable input that distinguishes the old and fixed behavior, or demonstrate that the published diagnosis needs correction. The production release gate remains open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 the fourteen closed criteria are accounted for: twelve as [SPEC §15](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) rows 'Deep review of v0.12.15' through 'Twelfth deep review' in 95410ba, 8f78a73 and 79a21e0, and two retired to the task that runs the gate
- [ ] #2 the thirteenth round's two P2s are fixed with a fixture that fails without the fix, and the SPEC §15 row and revision-log line land in the same commit as the code
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Drive the real publish refusal with a long valid tag, a matching long name and a mismatched upload URL. Parse the summary separately from the adjacent diagnostic. The same new fixture must lose the URL tail at the old 200-byte cap and retain it on the fixed workflow. External fixture review dispatched.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [fix and harness](https://github.com/ArborealML/Inference-Router/commit/806d10d) landed together with the SPEC revision and disposition. Its [CI harness job](https://github.com/ArborealML/Inference-Router/actions/runs/44139046103/job/134236447964) passed. The coordinator replayed that harness against both workflow versions. All 122 cases passed on the fixed version. Against 79a21e0 all three type_presence checks failed as intended, but line_bound_tail still passed with labels=1, tail=1 and maxmark=101. AC #2 remains open pending a discriminating test. The first canary attempt subsequently published successfully; its rerun refused in preflight.
<!-- SECTION:NOTES:END -->
