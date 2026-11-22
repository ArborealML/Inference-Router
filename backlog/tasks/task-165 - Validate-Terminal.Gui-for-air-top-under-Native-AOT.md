---
id: TASK-165
title: Validate Terminal.Gui for air top under Native AOT
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:09:00Z'
updated_date: '2026-11-22T04:48:00Z'
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
- [x] #1 Linux x64 Native AOT publish has zero trim warnings.
- [x] #2 Over SSH, a selected row survives updates and resize. Quitting restores the terminal.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Pin Terminal.Gui v2 centrally. Exercise a selectable table, stable identity during an update and resize, and clean exit. Qualify the Native AOT CLI and capture an SSH terminal readback before building the monitoring client.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The selectable terminal view introduced at 9eaa015 passed native qualification and real SSH tests. The final installed executable at 3339404 emitted zero trim warnings. Reordering and resizing retained selection, and quitting restored the shell. [Implementation and verification](https://github.com/ArborealML/Inference-Router/pull/79).
<!-- SECTION:NOTES:END -->
