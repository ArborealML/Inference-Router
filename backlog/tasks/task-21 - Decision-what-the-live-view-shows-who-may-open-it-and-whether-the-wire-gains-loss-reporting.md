---
id: TASK-21
title: >-
  Decision: what the live view shows, who may open it, and whether the wire
  gains loss reporting
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-17T02:18:00Z'
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

## Do this (about 5 minutes)

Three one-line answers, and the live view stops being un-startable:

1. **Who is it for** — an operator at a terminal, an agent debugging its own request, or both?
2. **Does it need its own credential?** Today the admin surface is unauthenticated and binds loopback only
   (SPEC §6.1). A view that listens on a shared box needs a token or stays loopback.
3. **Do you want a wire addition?** One new event kind on the event feed would report *how many* events a
   slow consumer missed. Without it, no viewer can ever say "N routing decisions were dropped" — it can
   only guess from holes in the sequence numbers.

Answer format: `TASK-21: both / own token / add the gap event`.
