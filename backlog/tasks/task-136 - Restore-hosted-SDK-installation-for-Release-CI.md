---
id: TASK-136
title: Restore hosted SDK installation for Release CI
status: In Progress
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-19T08:34:00Z'
updated_date: '2026-11-19T08:45:00Z'
labels: []
dependencies: []
priority: high
ordinal: 134000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Remove the preview-quality restriction in the two workflow setup steps to restore installation of a supported SDK. [Main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44488247949) failed before tests at 2d7ae36.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hosted CI installs the SDK required by global.json and reaches Release build and test.
- [ ] #2 CI and release workflow setup agree on SDK selection; record the corrected green hosted run.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Delete only dotnet-quality: preview in CI and release; preserve the action and global.json including latestFeature. Inspect the final diff and setup parity. Verify installed SDK, restore, Release build/test, Linux AOT and smoke on hosted CI. Report the precise CLI-selected SDK only with direct CLI evidence. Windows release is outside this verification.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [cancelled merge run](https://github.com/ArborealML/Inference-Router/actions/runs/44488195633) on b6f2ecf also failed the SDK-install step before cancellation, with the identical Linux x64 preview-channel error. Both runs track this one incident.

Plan on ed0a603 dispatched for review. The action translates latestFeature into the 11.0 channel; the failed SDK lookup explicitly requested preview quality.

Independent review of the plan on ed0a603 passed. Accepted the distinction between the SDK installed by setup and the SDK selected by a later CLI invocation. Implementation released.
<!-- SECTION:NOTES:END -->
