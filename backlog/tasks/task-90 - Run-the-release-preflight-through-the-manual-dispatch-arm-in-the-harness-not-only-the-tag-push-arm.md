---
id: TASK-90
title: >-
  Run the release preflight through the manual-dispatch arm in the harness, not
  only the tag-push arm
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-18T07:44:00Z'
updated_date: '2026-11-18T08:37:00Z'
labels:
  - ci
  - release
dependencies: []
priority: medium
ordinal: 89000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
CI already runs the release-gate harness: ci.yml has a job named "release gate harness (release.yml step bodies)" that runs python3 tools/relgate.py on a full checkout, and the harness extracts the real step bodies from release.yml and runs them against a stub gh. What it never runs is the manual-dispatch arm of the preflight step. release.yml:60 picks the tag from the workflow_dispatch input and forces dry_run when github.event_name is workflow_dispatch, and a hand-run against an existing tag is the only way to exercise the gate before a real tag is cut. Measured at 7d01520: grep -c "inputs.tag" tools/relgate.py is 0, and neither github.event_name nor GITHUB_REF_NAME appears anywhere in the harness, so every preflight case drives the tag-push arm. The arm a human drives — where a mistyped or absent tag must be refused — has no case. That is the class of gap that lets a release gate pass open while its own cases stay green.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 tools/relgate.py runs the release.yml preflight step body through the workflow_dispatch arm (tag from a workflow input, dry run forced) and refuses an absent or malformed tag with a non-zero exit.
- [ ] #2 The case discriminates: it fails against a step body that passes open, shown with the harness existing old-versus-new step-body comparison, no new machinery.
- [ ] #3 The harness case count and the CI job are named in the SPEC 15 row and revision-log line landing in the same commit as the harness change.
- [ ] #4 No tag is cut for this row: the live run stays TASK-42.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Re-measured by this seat at 7d01520 (2026-11-18T07:44Z): ci.yml:105 declares the release-gate-harness job and runs python3 tools/relgate.py; release.yml:60 is the workflow_dispatch arm; grep -c "inputs.tag" tools/relgate.py returns 0 and grep for github.event_name or GITHUB_REF_NAME in the harness returns nothing. The report that pointed here also cited a SPEC revision and task numbers that do not resolve on this board — SPEC highest row at HEAD is v0.12.36 and the repository carries two tags, both canaries — so the claim is recorded only as re-measured here, per decision-37, and the reporting seat was asked for raw command output. Sequencing: this edits tools/relgate.py, which [TASK-44](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-44%20-%20Close-the-open-release-gate-review-findings-SPEC-v0-12-15-onward.md) also edits for its thirteenth-round fixture; land one harness change at a time.

Measured again at 08:34Z, second seat, same answer: in tools/relgate.py the counts for workflow_dispatch, inputs.tag, event_name and GITHUB_REF_NAME are each 0. Use those four greps as the criterion — the string event.inputs.version describes nothing in this repository, and a row that hunts it would pass by irrelevance. AC #1 is harness-only and claimed.
<!-- SECTION:NOTES:END -->
