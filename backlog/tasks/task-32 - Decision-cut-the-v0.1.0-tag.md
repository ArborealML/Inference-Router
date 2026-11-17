---
id: TASK-32
title: 'Decision: cut the v0.1.0 tag'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T07:30:00Z'
labels:
  - release
dependencies: []
priority: medium
ordinal: 31000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A published release is the owner call, and decision-10 does not grant it: that decision settled which platforms are published, not that anything is published yet. The release workflow has only ever been exercised as a dry run, and there is no release on the repository. Grant it and TASK-28 runs the first real tag and the README install path can point at real assets. Decline it and the project keeps shipping build-from-source only, which is a legitimate posture: nothing in the router needs a published binary, and a tag is easy to keep but awkward to un-publish. Waiting on TASK-15 landing first, because a tag today would still build the dropped macOS legs.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Answer in one line: cut v0.1.0, or hold with a reason
<!-- AC:END -->
