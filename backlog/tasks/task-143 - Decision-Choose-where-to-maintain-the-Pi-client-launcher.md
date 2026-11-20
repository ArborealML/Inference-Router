---
id: TASK-143
title: 'Decision: Choose where to maintain the Pi client launcher'
status: Done
assignee:
  - '@claire'
created_date: '2026-11-20T00:25:00Z'
updated_date: '2026-11-20T00:27:00Z'
labels: []
dependencies: []
priority: high
ordinal: 141000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Claire selected a local Git repository for the temporary Pi launcher. The coordinator created the repository with no remote and verified the baseline against the three installed source files. Scheduling owns the scoped change after plan approval. Leave deployment separate; eventual Pi integration must allow retirement of CLM.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Record the chosen source repository and authorized scope, or the decision to defer.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
This resolves the source choice raised at 960ba7f. The local baseline and verification record are held outside the router repository.
<!-- SECTION:NOTES:END -->
