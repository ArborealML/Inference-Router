---
id: TASK-70
title: Check the author and committer identity of every commit in the push range
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T01:22:00Z'
updated_date: '2026-11-18T01:25:00Z'
labels: []
dependencies: []
priority: medium
ordinal: 70000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Decision-24 settled the identity question by leaving published history alone and preventing recurrence, so prevention is the deliverable: a CI step over the **range a push carries**, comparing each commit's author and committer against the repository's configured identity, failing the build and naming the offending commits. A tip-only check is worthless here, because the overrides this repository is cleaning up were per-commit choices made inside a range.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 a push whose range carries a commit authored or committed under any other identity fails the build and names the commits
- [ ] #2 the check reads the configured identity rather than a hard-coded name, so it holds for the owner and for an agent session alike
- [ ] #3 the already-published outliers sit below a recorded floor, and the floor says why in one sentence
<!-- AC:END -->
