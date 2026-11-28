---
id: TASK-186
title: 'Fix the Pi capacity warning: it aborts the turn and spills onto sibling agents'
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T09:22:00Z'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-19
dependencies:
  - TASK-188
priority: high
ordinal: 178000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
One agent's nearly-full turn produced a capacity warning that ended the inference instead of reaching the model as a normal turn, and the other agents on the same host failed at the same time. The installed Pi extension copy, when the estimated prompt leaves less room than the requested output, shrinks the output limit and appends a standalone system turn to the end of the message list on every payload attempt, or a chat-template keyword on the Qwen hosts. The engine keeps its prompt prefix parked per conversation, and a late standalone system turn is exactly what invalidates or breaks that prompt. Re-measured against the tracked copy under tools/pi-air: it does not inject, it refuses an over-budget request before submission and warns in the user interface, so the copy under review and the copy that runs differ and task 188 settles which is authoritative. In both copies selection and refusal state is held at extension scope, which the child agents of one session share, so one agent's near-full turn can change another agent's budget.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 A near-full turn on one agent does not change another agent's output budget, prompt or selection.
- [ ] #2 The capacity notice is delivered as a turn the engine accepts, and never aborts an inference already started.
- [ ] #3 A turn that cannot fit is refused before submission with a reason the router records once.
- [ ] #4 A pin reddens when the injected notice ends a stream instead of riding inside it.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
The installed extension is not the tracked source, and the injected system turn and chat-template keyword paths exist only in the installed copy. This task is gated on making the tracked copy the one that runs. (1) Per-agent state: selection, output budget and the near-full flag are keyed by model id at extension scope, so agents in one session share them; move them to the owning agent or thread and derive each payload's budget from that payload's measured prompt. Pin two concurrent agents where one is near-full, asserting the other's request bytes and headers are unchanged. (2) Prove the refusal path: an over-budget main request never reaches the network, and the refusal is recorded once with a reason a user can act on. (3) Keep the notice out of the payload: assert nothing is injected into the message list. (4) Keep an early-ended stream distinguishable in tests from a delivered notice. Gate: the existing pi-air node tests plus new per-agent and refusal tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The live Pi extension is /home/claire/inference-setup/pi-air/index.mjs (683 lines) plus catalog.mjs, loaded at session start by /home/claire/inference-setup/pi-clm.py with -e. That directory is NOT a git checkout, and the tracked copy this task reconciles is not in any repository on this host — the only tracked pi-clm repo here, /home/claire/src/arboreal/pi-clm, holds a 6-file launcher plus a 58-line air-pi.mjs shim that is a different, older file. So criterion 1 cannot run until the owner names the source of the 683-line extension.

Incident evidence, measured: the words "context exceeded" appear nowhere in the live extension or in AIR sources, so that refusal came from neither surface and matches Pi's own context guard. The AIR-side part is the engine warning plus stream handling, and the deployed router predates the containment fix, so the profile-label relaunch churn task-189 closed on main was live during the incident.
<!-- SECTION:NOTES:END -->
