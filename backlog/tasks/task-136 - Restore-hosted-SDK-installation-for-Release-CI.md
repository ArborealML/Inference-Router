---
id: TASK-136
title: Restore hosted SDK installation for Release CI
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T08:34:00Z'
updated_date: '2026-11-19T08:51:00Z'
labels: []
dependencies: []
priority: high
ordinal: 134000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Restored Ubuntu SDK setup by removing the preview channel restriction in CI and release. The shared global.json roll-forward policy is preserved.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Hosted CI installs the SDK required by global.json and reaches Release build and test.
- [x] #2 CI and release workflow setup agree on SDK selection; record the corrected green hosted run.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Delete only dotnet-quality: preview in CI and release; preserve the action and global.json including latestFeature. Inspect the final diff and setup parity. Verify installed SDK, restore, Release build/test, Linux AOT and smoke on hosted CI. Report the precise CLI-selected SDK only with direct CLI evidence. Windows release is outside this verification.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
[PR #17](https://github.com/ArborealML/Inference-Router/pull/17) merged at bac532b. Independent review accepted both workflow deletions and confirmed their exact implementation commit 3422493 against its parent. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44490496023) found SDK 11.0.100 installed, restored and built, passed 986 Release tests with zero skips, and passed Linux Native AOT and smoke. Verification covered Ubuntu CI and static parity with release workflow setup.
<!-- SECTION:NOTES:END -->
