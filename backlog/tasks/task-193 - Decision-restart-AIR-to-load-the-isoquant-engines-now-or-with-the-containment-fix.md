---
id: TASK-193
title: >-
  Decision: restart AIR to load the isoquant engines now, or with the
  containment fix
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-26T02:55:00Z'
updated_date: '2026-11-26T02:57:00Z'
labels: []
dependencies: []
priority: high
ordinal: 185000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Two hosted models are configured in the live AIR config and validated, but the running router loaded its config before that change and the runtime reload surface is not enabled in this process, so the change cannot take effect without a restart. A restart drops in-flight streams, including the sessions of the agents working on the containment fix. Answer needed: restart now, or restart once the containment fix merges so one restart carries both. Tell the coordinator which and it runs the restart.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One line: restart now, or restart after the containment fix merges.
- [ ] #2 Say whether to set the reload credential in the service environment at that restart so later config edits need no restart.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
State as of 2026-11-26: the live global config now carries a remote engine entry for the two hosted models, added and validated offline with the config dry-run verb (exit 0, no validation errors); the credential lives in a mode-600 key file beside that config, not in the repository, and nothing was committed for this. The upstream was probed directly: both model ids answer 200 on chat completions and report a 1048576-token window, and the zero-data-retention header the provider documents is optional, so no new header capability is needed. The running process loaded its config earlier and does not expose the reload route, which is only mapped when the reload bearer credential is present in the process environment at startup. So this change needs one restart, and the same restart can enable reload for later edits.
<!-- SECTION:NOTES:END -->
