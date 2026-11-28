---
id: TASK-201
title: Hold a client through an engine transition instead of ending its connection
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-26T12:20:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels:
  - wire
  - scheduling
milestone: m-2
dependencies:
  - TASK-190
  - TASK-187
priority: high
ordinal: 193000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Hold client through a bounded safe transition where possible. No output received is not proof upstream did not execute. Retry only after definite non-submission/non-execution or qualified idempotent same-operation path; ambiguity is a named outcome. TASK-190 owns drain/disposition.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Resize drains existing work or refuses without killing streams; report which.
- [ ] #2 Proven pre-submission refusal may retry once within original deadline/restrictions, keeping compatible keepalives.
- [ ] #3 No-output timeout, lost acknowledgment or generic uncertain failure never automatically replays; retain physical and financial liability.
- [ ] #4 Emitted semantic content is never spliced or silently rerun; report named cause in band with complete framing.
- [ ] #5 Slow downstream and stalled upstream are bounded; one terminal event and existing cancellation semantics.
- [ ] #6 Separately authorized independent replacement is a distinct attempt with its own reservation; original uncertainty remains.
- [ ] #7 Use the existing reason vocabulary to distinguish producer transition, context limit and stalled generation; no generic error hides the cause.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use existing keepalive/reason vocabulary, TASK-187 observer and TASK-190 drain. Capture both proven non-send and uncertain send; amend SPEC §§6–7 and 14 with code/tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Supersedes old no-output retry criterion. TASK-122 already recognizes uncertainty. No commercial charge policy introduced here.
<!-- SECTION:NOTES:END -->
