---
id: TASK-165
title: Validate Terminal.Gui for air top under Native AOT
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-22T04:09:00Z'
updated_date: '2026-11-22T04:23:00Z'
labels: []
milestone: m-15
dependencies: []
references:
  - src/Arboreal.InferenceRouter.Cli/Program.cs
ordinal: 160000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Exercise a selectable table and detail pane in the existing CLI using Terminal.Gui v2.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Linux x64 Native AOT publish has zero trim warnings.
- [ ] #2 Over SSH, a selected row survives updates and resize. Quitting restores the terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Pin Terminal.Gui v2 centrally. Exercise a selectable table, stable identity during an update and resize, and clean exit. Qualify the Native AOT CLI and capture an SSH terminal readback before building the monitoring client.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The design review confirmed the five-task sequence. Terminal.Gui v2 table and selection APIs are available. The implementer is building the first terminal view. Qualification uses a real SSH session.
<!-- SECTION:NOTES:END -->
