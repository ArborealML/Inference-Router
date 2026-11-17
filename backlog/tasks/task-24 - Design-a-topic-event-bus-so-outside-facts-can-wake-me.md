---
id: TASK-24
title: 'Design: a topic event bus so outside facts can wake me'
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:30:00Z'
labels:
  - tooling
  - notify
dependencies: []
priority: medium
ordinal: 24000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The gap: things that happen outside this session — a pull request merging, a decision landing on the board, a build finishing elsewhere, a machine coming back up — only reach me when a human tells me. The shape being proposed: a small durable event store with named topics; sources (scripts, services, a cron that checks a thing and emits when it changed) publish events, and an agent subscribes to the topics it cares about and gets woken the way it is woken today by asynchronous research results. Publisher identity and topic ownership matter, because otherwise anything can claim anything. What is needed from the owner is the authority model and where the store lives; the poller scripts and the subscription plumbing are ordinary work once the shape is fixed. Today the same effect is hand-rolled per case: a research service that pushes results, a session-to-session message channel, and a human passing word along.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The authority model is stated: who may publish to a topic, and how a publisher proves it
- [ ] #2 The store location and retention are decided, including what happens to events while no agent is running
- [ ] #3 A first transport is chosen (the existing push path for research results, or a new one)
- [ ] #4 One real source is wired end to end as the proof: a merged pull request, or a board decision landing
<!-- AC:END -->
