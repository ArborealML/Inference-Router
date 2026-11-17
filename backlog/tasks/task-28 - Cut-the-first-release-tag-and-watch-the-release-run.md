---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
labels:
  - release
  - ci
dependencies: []
priority: medium
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
decision-10 settled the platform surface: release linux-x64 and win-x64, nothing else. What is left is the first actual tag run — the release workflow has only ever been exercised as a dry run, and it refuses to publish unless every declared leg produced a verified artifact. Cutting a tag makes a public release, so this needs a one-line go-ahead (`cut v0.1.0`) from the main session rather than an assumption.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The workflow no longer declares or gates on osx artifacts (TASK-15's drop work has landed)
- [ ] #2 One tag run builds and smoke-tests linux-x64 and win-x64 on their own runners before publish
- [ ] #3 The published release carries both binaries plus SHA256SUMS, and the run is read rather than assumed green
- [ ] #4 The README install path points at the published assets
<!-- AC:END -->
