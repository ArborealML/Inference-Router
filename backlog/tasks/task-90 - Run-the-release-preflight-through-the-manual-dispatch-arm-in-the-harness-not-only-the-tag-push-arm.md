---
id: TASK-90
title: >-
  Run the release preflight through the manual-dispatch arm in the harness, not
  only the tag-push arm
status: In Progress
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T07:44:00Z'
updated_date: '2026-11-18T19:11:00Z'
labels:
  - ci
  - release
dependencies: []
priority: medium
ordinal: 89000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Prepare the reviewed release workflow change and its offline fixtures for initial tag resolution and both publication rechecks.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 tools/relgate.py runs the release.yml preflight step body through the workflow_dispatch arm (tag from a workflow input, dry run forced) and refuses an absent or malformed tag with a non-zero exit.
- [ ] #2 The case discriminates: it fails against a step body that passes open, shown with the harness existing old-versus-new step-body comparison, no new machinery.
- [ ] #3 The harness case count and the CI job are named in the SPEC 15 row and revision-log line landing in the same commit as the harness change.
- [ ] #4 No tag is cut for this row: the live run stays TASK-42.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Exercise the extracted release preflight step under workflow_dispatch. Map the input and event expressions to controlled test data, compare a valid dry-run input with absent and malformed values, and prove a pass-open control fails. Use the existing offline harness and record the verified case count in SPEC.

Prefer C# for new tooling. Assess reusing the release step extraction and old-versus-new fixture without duplicating the harness. Exercise the actual workflow script whichever test driver is chosen.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
At 3f20784 release.yml uses a bare commit lookup at lines 72, 502 and 690. Verify an exact tag and its built commit at all three reads. Pass input through the step environment and validate its entire value. Reuse the offline workflow extraction, fake service and historical comparison in tools/relgate.py. Duplicating that machinery in xUnit would exercise another harness. Coordinate edits with TASK-44.

Integration check on 2026-11-18 used read-only GitHub requests for the existing annotated canary. The proposed commits/tags/ lookup returned 422. The full commits/refs/tags/ lookup returned the commit matching the locally peeled tag. Update all three lookups and the fake service before integration.
<!-- SECTION:NOTES:END -->
