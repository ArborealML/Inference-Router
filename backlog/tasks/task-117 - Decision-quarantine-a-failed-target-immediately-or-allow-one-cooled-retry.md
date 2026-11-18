---
id: TASK-117
title: 'Decision: quarantine a failed target immediately or allow one cooled retry'
status: Blocked
assignee:
  - '@claire'
created_date: '2026-11-18T18:40:00Z'
updated_date: '2026-11-18T18:41:00Z'
labels: []
dependencies: []
ordinal: 115000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The owner chooses the response to a failed model switch. Reply A or B (about one minute). A latches the failed model/profile/config generation until explicit reset or configuration change, preserving capacity for a separately proven alternate. B allows one later same-target retry after cooldown, which may recover a transient fault but can displace the alternate again. Both refuse unproven ownership and readiness. This unblocks the failure policy in TASK-69. Record the answer with backlog task edit and cite it in the switching design.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The owner selects A or B; the chosen retry boundary and consequence for waiting work are recorded in the design decision.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Read the [switching design](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-69%20-%20Design-the-switching-router-never-stuck-sequenced-capacity-measured-switch-cost.md). Reply A or B here or run `backlog task edit 117 --append-notes "Owner selects A"` with your selected letter. The coordinator records the ruling.
<!-- SECTION:NOTES:END -->
