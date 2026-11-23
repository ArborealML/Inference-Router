---
id: TASK-177
title: Keep cancelled AIR tickets out of Pi error notifications
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-23T07:32:00Z'
updated_date: '2026-11-23T07:33:00Z'
labels: []
milestone: m-7
dependencies: []
references:
  - tools/pi-air/index.mjs
ordinal: 171000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
A cancelled ticket remains visible in /air-status. Reserve error notifications for failures.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 An isolated Pi session can inspect its cancelled ticket without receiving an error notification.
- [x] #2 A failed switch still produces an error notification.
- [ ] #3 The verified change lands on main and passes hosted CI.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Installed locally at b8e5670. An isolated Pi RPC test exercised both outcomes. Integration and hosted CI remain pending.
<!-- SECTION:NOTES:END -->
