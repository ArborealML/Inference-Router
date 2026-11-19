---
id: TASK-136
title: Restore hosted SDK installation for Release CI
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T08:34:00Z'
updated_date: '2026-11-19T08:46:00Z'
labels: []
dependencies: []
priority: high
ordinal: 134000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hosted CI and independent review of PR #17 wake landing. Main 2d7ae36 failed before tests while installing the SDK on Ubuntu.
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
[Failed main run](https://github.com/ArborealML/Inference-Router/actions/runs/44488247949) and [cancelled merge run](https://github.com/ArborealML/Inference-Router/actions/runs/44488195633) both failed to resolve the Linux x64 11.0 preview SDK. Plan review accepted the GA selection and installed-versus-selected evidence boundary. [PR #17](https://github.com/ArborealML/Inference-Router/pull/17) at 3422493 removes one preview-quality input per workflow. Both setup blocks match and global.json matches the failing baseline byte for byte. Independent commit review dispatched. Record the actual hosted installation and downstream checks.
<!-- SECTION:NOTES:END -->
