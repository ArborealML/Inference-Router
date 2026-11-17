---
id: TASK-21
title: >-
  Decision: what the live view shows, who may open it, and whether the wire
  gains loss reporting
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T05:55:00Z'
labels:
  - ui
  - wire
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Three things gate the live view (TASK-11). What it is for: an operator dashboard, or an agent-debug surface, or both. Whether it needs its own credential, since the admin surface is unauthenticated today and binds loopback only. And whether the event feed gains an explicit loss event (a gap plus a watermark) so a viewer can say "N routing decisions were dropped" instead of guessing from missing sequence numbers — that is a wire addition, so it needs your approval before I add it. Unblocks TASK-11.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The audience and the credential posture are stated
- [ ] #2 The loss-reporting event kind is approved or declined, with a one-line reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Read the decisions against SPEC §6.1 (admin surface: unauthenticated, loopback bind) and §10 (feed shape, replay, subscriber bounds): https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md — those two sections are the whole evidence base for the audience and credential questions, and §6.1 plus the §6.1 casing table is what a new event kind has to be added through. Per the owner instruction this decision is now the agents to take and record as a decision record; the wire addition is still a §6 change with contract tests, so the loss-event half lands as code only with a decision record naming it. TASK-11 wakes when that record exists.
<!-- SECTION:NOTES:END -->

## Links

- Admin surface as it ships: [SPEC §6](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L635) — unauthenticated, loopback bound.
- Feed a loss event would extend: [SPEC §10](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L946).
- Screens the answers shape: [SPEC §12.5](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md#L1079). Task that wakes on the answer: [TASK-11](https://github.com/ArborealML/Inference-Router/tree/main/backlog/tasks).

## Do this (about 5 minutes)

Three one-line answers, and the live view stops being un-startable:

1. **Who is it for** — an operator at a terminal, an agent debugging its own request, or both?
2. **Does it need its own credential?** Today the admin surface is unauthenticated and binds loopback only
   (SPEC §6.1). A view that listens on a shared box needs a token or stays loopback.
3. **Do you want a wire addition?** One new event kind on the event feed would report *how many* events a
   slow consumer missed. Without it, no viewer can ever say "N routing decisions were dropped" — it can
   only guess from holes in the sequence numbers.

Answer format: `TASK-21: both / own token / add the gap event`.
