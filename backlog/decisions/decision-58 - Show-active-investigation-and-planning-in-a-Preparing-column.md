---
id: decision-58
title: Show active investigation and planning in a Preparing column
date: '2026-11-18T18:24:00Z'
status: accepted
---
## Context

The board showed a worker inventorying supported model configurations under To Do. An assignee alone could not tell readers whether the investigation had begun.

## Decision

Insert Preparing between To Do and In Progress. Move a task there when its assignee starts investigating, gathering evidence, or developing its implementation plan. Describe the current preparation in the task. Multiple assignees can prepare independent tasks concurrently.

## Consequences

Move the task to In Progress when implementation begins, or Waiting when preparation is parked on an event such as an external review. Decision-4 continues to govern the implementation limit. The board checker accepts Preparing as a valid open status.
