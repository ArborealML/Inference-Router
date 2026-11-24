---
id: TASK-178
title: Start and stop AIR as a background user service on Linux
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-24T01:31:00Z'
updated_date: '2026-11-24T01:50:00Z'
labels: []
dependencies: []
ordinal: 172000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Run AIR independently of the invoking terminal with a corresponding stop command.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 air serve returns after the configured listener starts and reports startup failures.
- [x] #2 air stop gracefully stops the managed instance. Foreground mode remains available.
- [ ] #3 Land the CLI change on main after resolving the external lifecycle review and passing CI.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use a transient systemd user service, retain the invoking working directory and configured listener, signal readiness from the bound host, and exercise start, duplicate start, stop and bind failure with the safe smoke configuration.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The native Linux CLI passed detached start, HTTP readiness, duplicate refusal, graceful stop and occupied-port failure using an isolated user service. The foreground CI harness explicitly selects foreground mode.
<!-- SECTION:NOTES:END -->
