---
id: TASK-136
title: Restore hosted SDK installation for Release CI
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-19T08:34:00Z'
updated_date: '2026-11-19T08:39:00Z'
labels: []
dependencies: []
priority: high
ordinal: 134000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Independent plan review wakes the two-line hosted SDK repair. [Main CI](https://github.com/ArborealML/Inference-Router/actions/runs/44488247949) on 2d7ae36 failed in Ubuntu SDK installation before restore, build and tests.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Hosted CI installs the SDK required by global.json and reaches Release build and test.
- [ ] #2 CI and release workflow setup agree on SDK selection; record the corrected green hosted run.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Remove dotnet-quality: preview from CI and release. Keep setup-dotnet, the shared global.json input, 11.0.100 and latestFeature. Verify only those two lines changed, check parity and record the actual SDK and Release build/test and AOT in hosted CI. Do not dispatch a release or tag.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [cancelled merge run](https://github.com/ArborealML/Inference-Router/actions/runs/44488195633) on b6f2ecf also failed the SDK-install step before cancellation, with the identical Linux x64 preview-channel error. Both runs track this one incident.

Plan on ed0a603 dispatched for review. The action translates latestFeature into the 11.0 channel; the failed SDK lookup explicitly requested preview quality.
<!-- SECTION:NOTES:END -->
