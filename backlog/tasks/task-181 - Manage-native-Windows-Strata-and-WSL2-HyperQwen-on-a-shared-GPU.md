---
id: TASK-181
title: Manage native Windows Strata and WSL2 HyperQwen on a shared GPU
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-25T02:34:00Z'
labels: []
milestone: m-6
dependencies:
  - TASK-124
ordinal: 174000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Extend the managed cluster peer to coordinate native Windows Strata and WSL2 HyperQwen. Qualify on the Windows host once its operator makes it available.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ordinary queued requests switch the shared GPU in both directions. Each incumbent finishes first and releases the GPU. Observe the new resident and verify that the original waiting request streams from it.
- [ ] #2 Measure the effective HyperQwen window and verify it against the advertised profile before forwarding. An oversized request causes zero launches on that placement. A smaller observed window withdraws the profile.
<!-- AC:END -->
