---
id: TASK-186
title: 'Fix the Pi capacity warning: it aborts the turn and spills onto sibling agents'
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T09:22:00Z'
labels: []
dependencies: []
priority: high
ordinal: 178000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One agent's nearly-full turn produced a capacity warning that ended the inference instead of reaching the model as a normal turn, and the other agents on the same host failed at the same time. The Pi-side extension, when the estimated prompt leaves less room than the requested output, shrinks the output limit and appends a standalone system turn to the end of the message list on every payload attempt, or a chat-template keyword on the Qwen hosts. The engine keeps its prompt prefix parked per conversation, so a late standalone system turn is exactly what invalidates or breaks that prompt. Selection and refusal state is held at extension-instance scope, which the child agents of one session share, so one agent's near-full turn can change another agent's budget.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A near-full turn on one agent does not change another agent's output budget, prompt or selection.
- [ ] #2 The capacity notice is delivered as a turn the engine accepts, and never aborts an inference already started.
- [ ] #3 A turn that cannot fit is refused before submission with a reason the router records once.
- [ ] #4 A pin reddens when the injected notice ends a stream instead of riding inside it.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Reported live 2026-11-25. Still needed to fix the right cause: the error text the other agents showed and which session hit it first. Separate finding from the same host: the resident engine container's own image healthcheck fails every 30 seconds with exit 1, so the container manager reports it unhealthy while the router's probe reports it healthy and keeps serving; the two disagree and nobody has recorded that.
<!-- SECTION:NOTES:END -->
