---
id: TASK-90
title: >-
  Run the release preflight through the manual-dispatch arm in the harness, not
  only the tag-push arm
status: Done
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T07:44:00Z'
updated_date: '2026-11-18T19:20:00Z'
labels:
  - ci
  - release
milestone: m-9
dependencies: []
priority: medium
ordinal: 89000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Manual release preflight validates the complete tag and performs exact tag lookups. The offline CI harness exercises the real workflow step on both dispatch and push.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 tools/relgate.py runs the release.yml preflight step body through the workflow_dispatch arm (tag from a workflow input, dry run forced) and refuses an absent or malformed tag with a non-zero exit.
- [x] #2 The case discriminates: it fails against a step body that passes open, shown with the harness existing old-versus-new step-body comparison, no new machinery.
- [x] #3 The harness case count and the CI job are named in the SPEC 15 row and revision-log line landing in the same commit as the harness change.
- [x] #4 No tag is cut for this row: the live run stays TASK-42.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Exercise the extracted release preflight step under workflow_dispatch. Map the input and event expressions to controlled test data, compare a valid dry-run input with absent and malformed values, and prove a pass-open control fails. Use the existing offline harness and record the verified case count in SPEC.

Prefer C# for new tooling. Assess reusing the release step extraction and old-versus-new fixture without duplicating the harness. Exercise the actual workflow script whichever test driver is chosen.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Sol commit 2931662 integrated as [c62133e](https://github.com/ArborealML/Inference-Router/commit/c62133e). Both the worker and coordinator ran the complete 137-case offline harness with zero failures on their final runs. Reverting each of the three tag lookup paths independently failed its control. Read-only GitHub verification matched the peeled annotated canary commit. No release or tag was created. Bounded commit review dispatched on 2026-11-18.
<!-- SECTION:NOTES:END -->
