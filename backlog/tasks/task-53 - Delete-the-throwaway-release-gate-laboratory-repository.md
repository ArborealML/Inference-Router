---
id: TASK-53
title: Delete the throwaway release-gate laboratory repository
status: Waiting
assignee:
  - '@claire'
created_date: '2026-11-17T19:47:00Z'
updated_date: '2026-11-18T20:57:00Z'
labels:
  - release
milestone: m-9
dependencies: []
priority: low
ordinal: 53000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The laboratory repository the release-gate battery ran in (created under the user account, then archived: a push is refused as read-only) still exists. The working token cannot delete it: `DELETE /repos/{owner}/{repo}` answers 403 `Must have admin rights to Repository` because the `delete_repo` scope is absent. Two minutes either way: delete it in the browser (repository Settings, Danger Zone, Delete this repository), or refresh the token with `delete_repo` and run `gh api -X DELETE /repos/{owner}/{repo}`. TASK-49 records the completed laboratory measurements. Retain the archived laboratory until the first real release. Nothing else is waiting on it: the repository is read-only, so it cannot be pushed to or mistaken for a live target.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 the repository no longer exists, and the task records which route was taken
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The owner's call, recorded: keep the laboratory until the first real release tag run lands, then delete it. So this row is parked, not blocked — wake condition: only after that tag run does the delete become due, and it stays a two-minute browser act. The token route is still open for whoever wants it: a refresh with the delete scope, then the API call.

Before deletion, preserve redacted raw status, object IDs and digest evidence from the lab in a tracked record, with its provenance. The first production release remains the wake condition.
<!-- SECTION:NOTES:END -->
