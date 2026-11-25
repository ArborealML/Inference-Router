---
id: TASK-186
title: 'Fix the Pi capacity warning: it aborts the turn and spills onto sibling agents'
status: To Do
assignee:
  - '@chatswarm'
created_date: '2026-11-25T09:22:00Z'
updated_date: '2026-11-25T10:33:00Z'
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
Evidence from the owner 2026-11-25: several sessions failed about the same time, most with connection closed, and at least one reported context exceeded with more than 100k tokens of window apparently still free. AIR logged no refusal, no stream error and no engine event in that window, so the abort is invisible on the router side. The resident container's own image healthcheck has been failing every 30 seconds with exit 1, so the container manager calls it unhealthy while the router's probe calls it healthy and keeps serving; the two disagree and neither records it. Engine logs from the window show 140k-token prompts with 137k reused from the conversation cache and parked cache around 16-18 GiB of a 24 GiB budget. A diagnosis pass is dispatched to the external reasoning service (2026-11-25, extra-high tier) covering the warning delivery, the client capacity arithmetic, the connection-closed cluster and the observability gap.
<!-- SECTION:NOTES:END -->
