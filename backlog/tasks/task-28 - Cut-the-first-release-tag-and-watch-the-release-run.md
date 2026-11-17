---
id: TASK-28
title: Cut the first release tag and watch the release run
status: Waiting
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T05:58:00Z'
updated_date: '2026-11-17T07:30:00Z'
labels:
  - release
  - ci
dependencies:
  - TASK-15
priority: medium
ordinal: 28000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
decision-10 settled the platform surface: release linux-x64 and win-x64, nothing else. What is left is the first actual tag run — the release workflow has only ever been exercised as a dry run, and it refuses to publish unless every declared leg produced a verified artifact. Cutting a tag makes a public release, so this needs a one-line go-ahead (`cut v0.1.0`) from the main session rather than an assumption.

**Links**
- Workflow a tag runs: [release.yml](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml); preflight refusal at [96-104](https://github.com/ArborealML/Inference-Router/blob/main/.github/workflows/release.yml#L96-L104).
- Where it appears: [releases](https://github.com/ArborealML/Inference-Router/releases). Commands: `git tag -a v0.1.0 -m v0.1.0 && git push origin v0.1.0`, then `gh run watch <id>`.
- Must land first: [TASK-15](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks). Decision behind the platform set: [decision-10](https://github.com/ArborealML/Inference-Router/tree/main/backlog/decisions).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One tag run builds and smoke-tests linux-x64 and win-x64 on their own runners before publish
- [ ] #2 The published release carries both binaries plus SHA256SUMS, and the run is read rather than assumed green
- [ ] #3 The README install path points at the published assets
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Precondition, not an acceptance criterion: the workflow must declare and gate two platforms first (TASK-15). Cut off as a criterion because that change already has an owner and a criterion of its own. Also gated by the owner task asking whether to publish at all: a tag creates a public release, and no decision on the board grants that. Status returns to Waiting when both clear.

Precondition, not a criterion (it is TASK-15 AC #2 and has one owner): the workflow must no longer declare or gate on osx artifacts. Owner authorisation to publish at all is a separate task; TASK-28 is Blocked on it.
<!-- SECTION:NOTES:END -->
