---
id: TASK-12
title: 'External design: CI and tag-release workflow with tested AOT binaries'
status: In Progress
assignee:
  - '@flagship'
created_date: '2026-11-16T20:50:00Z'
labels:
  - ci
milestone: v0.10 - live view and robustness parity
dependencies: []
priority: high
ordinal: 12000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Design pass handed to the external flagship reviewer; it returns complete commented workflow files plus repo diffs. The exact request is logged in the private research log.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Preflight fails before any build when a release for the tag already exists
- [ ] #2 Build and tests run on every push to main and on pull requests
- [ ] #3 Stripped AOT binaries for linux-x64, win-x64, osx-arm64 and osx-x64 built on native runners
- [ ] #4 Every binary is smoke-verified headlessly with the inert sample before the release exists
- [ ] #5 No CI job can invoke a lifecycle command or a real engine
<!-- AC:END -->
