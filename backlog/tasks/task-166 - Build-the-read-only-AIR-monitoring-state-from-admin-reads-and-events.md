---
id: TASK-166
title: Build the read-only AIR monitoring state from admin reads and events
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T04:09:00Z'
labels: []
milestone: m-15
dependencies:
  - TASK-165
references:
  - SPEC.md
ordinal: 161000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Read status periodically, follow the global event stream and fetch known tickets. Keep this C# state independent of terminal widgets for reuse by Blazor.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Current snapshots supply health, loaded model, switch target, context, slots, in-flight counts and queue depths across configured engines.
- [ ] #2 A known ticket can be followed by ID using existing read-only endpoints. A fresh snapshot corrects outdated gauges.
<!-- AC:END -->
