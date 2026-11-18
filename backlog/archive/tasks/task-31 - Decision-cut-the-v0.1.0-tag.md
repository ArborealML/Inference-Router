---
id: TASK-31
title: 'Decision: cut the v0.1.0 tag'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T07:28:00Z'
labels:
  - release
dependencies: []
priority: medium
ordinal: 30000
milestone: m-9
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A public release is the owner call, and decision-10 does not grant it: that decision settled which platforms are published, not whether to publish anything yet. TASK-28 carries the work of cutting the tag and reading the run, but it cannot start until this answers yes or no. Cost: about 2 minutes. Command: git tag -a v0.1.0 -m v0.1.0 && git push origin v0.1.0 — or say hold and the release stays a dry run. What changes under each option: cut, and the public releases page gains linux-x64 and win-x64 binaries with SHA256SUMS, the README install path can point at real downloads, and TASK-14 release badge closes; hold, and the repository keeps telling readers to build from source, and TASK-28 stays parked. Answer in one line on this task: cut v0.1.0, or hold because REASON.
<!-- SECTION:DESCRIPTION:END -->
