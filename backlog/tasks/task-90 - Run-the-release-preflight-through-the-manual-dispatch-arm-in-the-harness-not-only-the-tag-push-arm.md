---
id: TASK-90
title: >-
  Run the release preflight through the manual-dispatch arm in the harness, not
  only the tag-push arm
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T07:44:00Z'
updated_date: '2026-11-18T18:46:00Z'
labels:
  - ci
  - release
dependencies: []
priority: medium
ordinal: 89000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when the external release-preflight plan review returns. The implementation must validate the manual tag input and prove its refusal behavior through the extracted workflow step.
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
Re-measured by this seat at 7d01520 (2026-11-18T07:44Z): ci.yml:105 declares the release-gate-harness job and runs python3 tools/relgate.py; release.yml:60 is the workflow_dispatch arm; grep -c "inputs.tag" tools/relgate.py returns 0 and grep for github.event_name or GITHUB_REF_NAME in the harness returns nothing. The report that pointed here also cited a SPEC revision and task numbers that do not resolve on this board — SPEC highest row at HEAD is v0.12.36 and the repository carries two tags, both canaries — so the claim is recorded only as re-measured here, per decision-37, and the reporting seat was asked for raw command output. Sequencing: this edits tools/relgate.py, which [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) also edits for its thirteenth-round fixture; land one harness change at a time.

Measured again at 08:34Z, second seat, same answer: in tools/relgate.py the counts for workflow_dispatch, inputs.tag, event_name and GITHUB_REF_NAME are each 0. Use those four greps as the criterion — the string event.inputs.version describes nothing in this repository, and a row that hunts it would pass by irrelevance. AC #1 is harness-only and claimed.

The manager dispatched the plan review on 2026-11-18 covering tag-qualified lookup, whole-input validation, fake release-service fixtures and the C# versus existing harness tradeoff.
<!-- SECTION:NOTES:END -->
