---
id: TASK-21
title: >-
  Decision: ratify the live-view shape - audience, credential posture, and no
  new feed event kind
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-17T16:42:00Z'
labels:
  - ui
  - wire
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
**Adopted; nobody is being asked anything:** 1 operator-first — a read-only view over the existing admin reads plus the SSE feed, counts scoped to retained events, no new event kinds; 2 `/admin` stays unauthenticated and loopback-only, reached over an operator-managed tunnel, no token on it; 3 no new feed event kind — continuity comes from sequence holes plus the exclusive cursor. Each was checked against [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §6, §10, §12.5 and against the ring's eviction semantics in code before being recorded.

**Remaining act (the coding session, about 30 minutes):** write those three postures into SPEC §6, §10 and §12.5 as a revision, with the revision-log line in the same commit, then start [TASK-11](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-11%20-%20Blazor-live-view-screens-plus-the-event-stream-additions-they-need.md), parked behind this answer.

**Links:** [SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §6 (the admin surface and its error casing), §10 (the feed), §12.5 (the screens); the adjacent and deliberately separate `/v1` question: [TASK-34](https://github.com/ArborealML/Inference-Router/blob/main/backlog/tasks/task-34%20-%20Decision-what-the-caller-sees-when-a-generation-stalls-or-a-client-stops-reading.md) (answered).
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 #1 The audience and the credential posture are stated
- [ ] #2 #2 The loss-reporting event kind is approved or declined, with a one-line reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**How these were reached.** The external research and review service recommended all three on 2026-11-17; this board checked each against
[SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §6 and §10 and the ring's eviction behaviour in code. Findings and dispositions are here and in SPEC; request bookkeeping is not published.

**1 — Operator dashboard first, not an agent debugger.** The feed reconstructs routing choices, engine-state transitions
and terminal outcomes *within retention*; it never reconstructs a request trace, and an evicted transition cannot prove
current engine state. So the view bootstraps current state from the admin reads and labels the rest observed: read-only
over the admin reads plus the SSE feed, counts scoped to retained events, no new event kinds. A debug surface would be
new event and field contracts — stages, timings, correlation ids — a wire addition with its own approval, not a screen.

**2 — `/admin` stays unauthenticated and loopback-only; no token.** The view is co-located, reached over an operator tunnel. One admin token authorises engine start/stop as much as viewing, so a stolen token is control,
not visibility; a read-only credential is honest only if a read-only endpoint exists — it does not.
Worth doing now, not a wire change: browser-request hardening on a loopback control plane (Host
and Origin checks, safe methods), since a hostile page can make a browser hit `localhost`. Remote or multi-user access
returns as its own approval: TLS plus an authenticated read-only surface, separate from the mutating one.

**3 — No loss/gap event kind.** Loss is subscriber-relative: an eviction event in the shared ring describes
something that may affect nobody while consuming ring capacity, and a gap is already derivable from sequence holes plus
the exclusive cursor.

**Owed by this task:** one SPEC revision recording the three postures, revision-log line in the same commit; TASK-11
parks behind it.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
