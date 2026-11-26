---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T00:54:00Z'
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
- [ ] #1 Every inference request from the extension carries a priority the router parses.
- [ ] #2 /priority sets the value for the session and reports it back.
- [ ] #3 The value survives a session restart and shows in the extension's status view.
- [ ] #4 A rejected priority value produces the router's stated refusal, not a silent default.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Both sides in one branch, because criterion 4 needs the router to refuse. (1) Router ingress: today ServerApp parses X-Inference-Priority with ThreadPriority.TryParse and a malformed value falls through to Normal silently. Change that to a 400 with an additive named lowercase error code when the header is present but unparseable; an absent header still means Normal. SPEC 6 and 6.1 state it and the wire test replays it. (2) Extension: a session-scoped priority, default unset so no header is sent; /priority with no argument prints the current value and the five accepted class names, with an argument sets it, accepting only background, low, normal, high, interactive with an optional colon-sub 0..255. (3) Header plumbing: set the header on every agent request beside X-Inference-Thread, and make the direct-registry path, which merges its own headers, carry it too. (4) Persistence and status: store it with the existing selection state so it survives a restart, and show it in the context command's status output. (5) Docs: README feature line, docs/ai-integration.md priority row, and the SPEC line that lists the extension's headers. Gates: build with warnings as errors, the focused ingress and wire tests, and the existing pi-air node tests plus a new one pinning the header on both request paths.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Extension-side only; the router surface already exists (SPEC §6 header list). Pairs with TASK-183: priority and required context are the two demand axes a caller owns.

Verified on main: the router reads the header in ServerApp and a malformed value is silently ignored, so criterion 4 cannot be met extension-side alone. Router-side refusal is a wire change and needs the plan review before code.
<!-- SECTION:NOTES:END -->
