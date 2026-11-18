---
id: TASK-16
title: Survey which inference APIs the router should front
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-16T23:02:00Z'
updated_date: '2026-11-18T20:47:00Z'
labels: []
milestone: m-12
dependencies:
  - TASK-14
priority: low
ordinal: 16000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-14 lands. Then dispatch the upstream adapter comparison for Strata, Maya and other candidate engine APIs against the existing provider seam. The client ingress review is complete. The two other reviews in that batch covered host exclusivity and switch failure policy.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Request submitted to the external research service after TASK-14 lands, and its answer mirrored into this task body
- [ ] #2 Candidate APIs compared on what they cost us: streaming shape, tool/thinking blocks, cancellation, context-window signalling, auth, AOT-safe JSON story
- [ ] #3 A decision recorded in SPEC: which adapters are in for v0.2, which are explicitly out, and the engine 'kind' taxonomy that supports both
- [ ] #4 Any new kind gets a fake-handler wire test before real engine wiring
- [ ] #5 Any proposed Responses route must pass typed sync and SSE fixtures plus a real Codex tool round with matching call IDs. Unsupported response history, hidden reasoning and cache counts are refused or reported unavailable.
- [ ] #6 Qualify Responses tool calls through completion and cancellation with matching IDs and exactly one terminal outcome. Replay the existing Chat fixtures against the same build.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
First pass received 2026-11-18. At 854d7ad ServerApp registers Chat and legacy Completions; Responses is a candidate for Codex. Retain Chat for pi and legacy Completions for existing clients. The review recommends declining vendor-specific client facades. Compare those recommendations with the upstream adapter evidence. TASK-108 owns queued Chat keepalives.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->

created: 2026-11-17T05:55:00Z

---

Assignee is the external service again now that the owner has authorised handing this over: the deliverable is a comparison of upstream API shapes and a recommendation of which one or two adapters to build, which is a research deliverable, not local work. Dispatch still waits on TASK-14 by sequencing, so the task stays To Do and carries that dependency.

---

<!-- COMMENTS:END -->
