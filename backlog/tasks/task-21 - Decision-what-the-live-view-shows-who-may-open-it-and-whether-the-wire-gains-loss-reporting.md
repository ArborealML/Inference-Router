---
id: TASK-21
title: Document the adopted operator dashboard and feed contract
status: Waiting
assignee:
  - '@gpt-6.2-sol-stream-delivery'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-20T22:41:00Z'
labels:
  - ui
  - wire
milestone: m-11
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake on independent integration review and hosted CI for the [dashboard documentation draft](https://github.com/ArborealML/Inference-Router/pull/67) at bfcc169. The integrated revision preserves the MiniMax entry and the subsequent switch-test citation. Independent commit review passed on 82ec2f1. The SPEC-only change documents the existing admin reads, retained event counts, replay cursor and sequence gaps, and the 256-entry buffer for HTTP event-feed subscribers. It distinguishes recommended loopback deployment from listener configuration and describes the credentials for reload and reset. Delivery owns the landing after both checks pass.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The audience and the credential posture are stated
- [ ] #2 The loss-reporting event kind is approved or declined, with a one-line reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
**How these were reached.** The external research and review service recommended all three on 2026-11-17; this board checked each against
[SPEC.md](https://github.com/ArborealML/Inference-Router/blob/main/SPEC.md) §6 and §10 and the ring's eviction behaviour in code. Findings and dispositions are here and in SPEC.

**1 — Operator dashboard first, not an agent debugger.** The feed reconstructs routing choices, engine-state transitions
and terminal outcomes *within retention*; it never reconstructs a request trace, and an evicted transition cannot prove
current engine state. So the view bootstraps current state from the admin reads and labels the rest observed: read-only
over the admin reads plus the SSE feed, counts scoped to retained events, no new event kinds. A debug surface would be
new event and field contracts — stages, timings, correlation ids — a wire addition with its own approval, not a screen.

**2 — `/admin` stays unauthenticated and loopback-only; no token.** The view is co-located, reached over an operator tunnel. One admin token authorises engine start/stop as much as viewing, so a stolen token is control,
not visibility; a read-only credential is honest only if a read-only endpoint exists — it does not.
Worth doing now, not a wire change: browser-request hardening on a loopback control plane (Host
and Origin checks, safe methods), since a hostile page can make a browser hit `localhost`. Remote or multi-user access returns as its own approval: TLS plus an authenticated read-only surface.

**3 — No loss/gap event kind.** Loss is subscriber-relative: an eviction event in the shared ring describes
something that may affect nobody while consuming ring capacity, and a gap is already derivable from sequence holes plus
the exclusive cursor.

**Owed by this task:** one SPEC revision recording the three postures, revision-log line in the same commit; TASK-11
parks behind it.

Superseded log entries for this task: [board trim, 2026-11-17](https://github.com/ArborealML/Inference-Router/blob/main/backlog/archive/board-trim-2026-11-17.md).
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
