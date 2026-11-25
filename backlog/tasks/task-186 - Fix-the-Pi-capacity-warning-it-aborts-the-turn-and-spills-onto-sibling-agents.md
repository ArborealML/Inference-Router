---
id: TASK-186
title: 'Fix the Pi capacity warning: it aborts the turn and spills onto sibling agents'
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T09:22:00Z'
updated_date: '2026-11-25T10:49:00Z'
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
External diagnosis (2026-11-25, extra-high) ingested; neither Pi interference nor a common engine failure is proven. Verified here before accepting: the Pi extension is tracked in this repository under tools/pi-air, and the installed copy differs from the tracked copy — the tracked one sends the profile header and injects no warning, the installed one injects the warning, drops the profile header and caps output at 32768 (65536 for one family). The reviewed artefact is not the deployed artefact, so this task describes the installed behaviour and must name which copy is authoritative. Second verified claim: SPEC §6 states a clean upstream end-of-stream at a block boundary adds no bytes, so a stream that ends without its terminator can read as completed on the router and as connection closed on the client. Arithmetic from the review, checked against live numbers: with a measured window of 262144 and a 140k prompt there is 122k of room, and the installed output caps keep the total inside the window, so the client's own numbers cannot account for 100k of missing room; near the boundary room leaves nothing for templated or tail-role tokens or tokenizer error, which is a real but smaller defect. Reused key-value tokens still count toward the window. Cause ranking held: shared client state across child agents first, a shared engine fault second, the missing terminator third. Next: request-local frozen budget and selection on the Pi side, no late appended turn.
<!-- SECTION:NOTES:END -->
