---
id: TASK-22
title: 'Decision: macOS runner minutes, and when to cut v0.1.0'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
labels:
  - release
  - ci
dependencies: []
priority: medium
ordinal: 22000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The release workflow builds and smoke-tests per-platform binaries; the macOS legs need paid runner minutes enabled, and the release workflow only runs on a tag. Both are yours. Say either "enable macOS minutes and tag v0.1.0 now" or "hold", and TASK-15 and the release task move accordingly. Unblocks TASK-15.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Minutes are enabled or explicitly declined
- [ ] #2 A v0.1.0 tag is agreed, or held with the reason
<!-- AC:END -->

## Do this (about 2 minutes)

1. **macOS runners**: <https://github.com/ArborealML/Inference-Router/settings/actions> and the billing page. macOS minutes cost more than Linux.
   Say `enable macOS` or `decline macOS`.
2. **First release**: the release workflow only runs on a tag, and it builds four platform binaries and
   smoke-tests each on its own OS before publishing anything. Saying `tag v0.1.0` is enough — I cut the
   tag and watch the run. Say `hold` (and until what) if you would rather not publish yet.
