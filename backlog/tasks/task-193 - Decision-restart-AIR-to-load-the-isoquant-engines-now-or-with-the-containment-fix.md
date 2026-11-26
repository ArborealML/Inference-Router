---
id: TASK-193
title: >-
  Decision: restart AIR to load the isoquant engines now, or with the
  containment fix
status: Done
assignee:
  - '@claire'
created_date: '2026-11-26T02:55:00Z'
updated_date: '2026-11-26T06:02:00Z'
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
- [x] #1 One line: restart now, or restart after the containment fix merges.
- [x] #2 Say whether to set the reload credential in the service environment at that restart so later config edits need no restart.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Owner answer 2026-11-26 06:00Z: restart now, and set the reload credential at that restart. Rationale accepted: no agent run was in flight, so the restart cost one lost turn rather than a worker's work, and enabling reload stops later config edits from accumulating restarts. The coordinator generated a 32-byte bearer into a mode-600 file beside the live config, placed it in the user service manager environment, and the service restarted; the config itself was not edited, so the restart carried only the hosted engines and the reload gate, and a validated pre-restart copy was kept as rollback. The shipped binary already maps the reload route, confirmed from the installed binary's route table. Live after the restart, unauthenticated requests to the reload route answer 401 and an authenticated read answers 200 with a strong generation tag; a conditional apply answered 200 with the generation number and no stream was dropped. The reload surface is specified in SPEC §7.7; the live configuration itself lives outside the repository and is not a tracked artifact.
<!-- SECTION:NOTES:END -->
