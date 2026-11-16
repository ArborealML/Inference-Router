---
id: TASK-2
title: Ticket-aware Pi session surface
status: In Progress
assignee: []
created_date: '2026-11-16T20:40:00Z'
updated_date: '2026-11-16T22:55:00Z'
labels: []
dependencies: []
ordinal: 2000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Closes the AIR side of the Pi protocol in SPEC 13.3: replay of session events filtered by ticket, validation of a 'ready' ticket when the completion arrives, AIR model metadata on /v1/models, and a POST cancel alias.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Pi can replay only the events for its own ticket
- [ ] #2 A ready ticket is validated against the switch that produced it
- [ ] #3 Wire tests cover each addition and the OpenAI-compatible surface stays unchanged
<!-- AC:END -->
