---
id: TASK-5
title: Curate the Groma architecture map
status: In Progress
assignee: []
created_date: '2026-11-16T20:40:00Z'
labels: []
dependencies: []
ordinal: 5000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
First scan produced 18 elements. The C# scanner does not evaluate source generators or ASP.NET references, so treat those scan warnings as scan limits, not repo defects, and place components deliberately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 groma lint is clean or every finding is dispositioned
- [ ] #2 Components are named and related the way the spec describes them (router core, scheduler, engine runtime, config, wire surface)
<!-- AC:END -->

**Owner instruction (on record):** curate this repository's architecture with Groma — run
`groma agent-instructions` and follow its curation workflow, describing the architecture as it exists
today. **Do not change application code** as part of this task.

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 `groma agent-instructions` read and its curation workflow followed
- [ ] #2 The C# scanner is working again (the map currently reports it as not evaluating source
  generators or ASP.NET references — restore it, or record precisely why it cannot run here)
- [ ] #3 The map describes today's architecture: library seams (RouterCore, queues, scheduler, prefix
  trie, engine runtime, config loader, event ring), HTTP surface, CLI, bench harness, CI tools
- [ ] #4 Scan warnings are triaged into "scan limit" vs "repo defect" and the map or this task records
  which is which
- [ ] #5 No application code changed; only `groma/` (and this board) move
<!-- AC:END -->
