---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T08:37:00Z'
labels: []
dependencies: []
priority: high
ordinal: 177000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The router already admits a caller priority on the inference path and uses it in scheduling, but the Pi-side extension never sends one, so every Pi session arrives at the configured default. Add the header on every request and a /priority slash command that sets it per session, with the current value shown in the status surface. The scheduler's use of priority for drain order is TASK-72 and is not part of this task.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Every inference request from the extension carries a priority the router parses, including a fresh session.
- [ ] #2 /priority sets the value for the session and reports it back.
- [ ] #3 The value survives a session restart and shows in the extension's status view.
- [ ] #4 A rejected priority value produces the router's stated refusal, not a silent default.
- [ ] #5 A malformed priority is refused before any scheduler mutation, dispatch or stream start, in the existing error envelope.
- [ ] #6 The session value is the one present on the final outbound request on both request paths and after a session restore.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two deliverables, one branch, and criterion 4 needs both. (1) Router, landed: validate the header before any scheduler mutation, dispatch or stream start, and refuse with a 400 that reuses the existing lowercase error envelope by adding a code value rather than a field or shape; an absent header keeps the default. (2) Extension: initialise the session priority to normal and send it on every inference request rather than inheriting the router default. The value is authoritative at final header assembly, replacing case-insensitive duplicates, and both request paths and any separately assembled retry are tested on the final outbound request. An invalid command argument is refused locally with stored state unchanged, and a refusal is never answered by a headerless retry. (3) Persistence: session-bound storage, hydrated and validated before the first dispatch; missing legacy state becomes normal, corrupt state is a visible reset, and a failed save is reported, not promised. (4) Semantics: the value applies to subsequent requests for every thread in the session, snapshotted with the thread id; no thread rotation and no reprioritising queued or running work. A boost stays for the session and is shown on resume and in the context view.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Router half landed at 0406520 on branch feat/pi-priority-header: 400 invalid_priority in the existing lowercase envelope, thrown while headers are read, additive §6.1 code and status map, plus PriorityIngressTests over absent, empty, malformed, duplicated, cased and out-of-range values, ordinary and streaming. My checks in its worktree: build clean, PriorityIngressTests 22 passed, the wire/priority/ingress set 97 passed. That seat timed out twice without committing, so I verified its tree and committed under the implementer identity. Client half targets the live extension /home/claire/inference-setup/pi-air/index.mjs: the header block at :420-432 sets thread id, title, tmux session, preferred profile and required context, and /priority registers alongside /context at :620; nothing there mentions priority. Two seats spent their budget reading without editing, so this dispatch is a bounded edit with a backup and a listener header dump as the wire check.
<!-- SECTION:NOTES:END -->
