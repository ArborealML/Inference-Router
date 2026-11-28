---
id: TASK-194
title: >-
  Run the router under a persistent service unit that carries the reload
  credential
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-26T06:01:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels:
  - config
  - lifecycle
milestone: m-8
dependencies: []
priority: medium
ordinal: 186000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router currently runs as a transient user service unit with no restart policy and no environment, so the reload credential put there by the running operator disappears at logout or reboot, and a process that dies is not started again. Make the service durable: a real unit file that carries the credential, restarts within a bound, and names the rollback path when it fails to start.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The router starts from a persistent user service unit that survives logout and reboot and reads the reload credential from the unit environment or a credential file, never from a shell or argv.
- [ ] #2 A failed start is reported rather than silent: the unit reaches a named failed state within one minute and the operator can restart the previous configuration in one command.
- [ ] #3 No tracked file, commit or task exposes the credential value; the unit references a mode-600 path outside the repository.
<!-- AC:END -->
