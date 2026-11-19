---
id: TASK-136
title: Restore hosted SDK installation for Release CI
status: Preparing
assignee:
  - '@gpt-6.2-sol-release'
created_date: '2026-11-19T08:34:00Z'
updated_date: '2026-11-19T08:38:00Z'
labels: []
dependencies: []
priority: high
ordinal: 134000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Main 2d7ae36 stopped before tests in the Ubuntu SDK installation step. The installer could not locate a Linux x64 SDK for the 11.0 preview channel. Check the setup inputs against the repository SDK pin. [Failed main run](https://github.com/ArborealML/Inference-Router/actions/runs/44488247949).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hosted CI installs the SDK required by global.json and reaches Release build and test.
- [ ] #2 CI and release workflow setup agree on SDK selection; record the corrected green hosted run.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Inspect both workflows and installer logs. Preserve the SDK pin, identify the smallest workflow correction and verify it on hosted CI.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [cancelled merge run](https://github.com/ArborealML/Inference-Router/actions/runs/44488195633) on b6f2ecf also failed the SDK-install step before cancellation, with the identical Linux x64 preview-channel error. Both runs track this one incident.
<!-- SECTION:NOTES:END -->
