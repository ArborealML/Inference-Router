---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: Preparing
assignee:
  - '@chatswarm'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T01:13:00Z'
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
- [ ] #5 Every inference request from the extension carries a priority the router parses, including a fresh session.
- [ ] #6 /priority sets the value for the session and reports it back.
- [ ] #7 The value survives a session restart and shows in the extension's status view.
- [ ] #8 A rejected priority value produces the router's stated refusal, not a silent default.
- [ ] #9 A malformed priority is refused before any scheduler mutation, dispatch or stream start, in the existing error envelope.
- [ ] #10 The session value is the one present on the final outbound request on both request paths and after a session restore.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two deliverables, one branch, and criterion 4 needs both. (1) Router: validate the header before any scheduler mutation, dispatch or stream start, and terminate with a 400 that reuses the existing lowercase error envelope by adding a code value rather than a field or shape. An absent header keeps the default; a present-but-invalid one is refused. Cover absent, empty, invalid, duplicated and differently cased headers, sub values 0, 255 and 256, partial numbers, ordinary and streaming, and assert zero dispatch mutation plus source-generated serialisation. (2) Extension: initialise the session priority to normal and send it on every inference request rather than inheriting the router default. The value is authoritative at final header assembly, replacing case-insensitive duplicates, and both request paths and any separately assembled retry are tested on the final outbound request. An invalid command argument is refused locally with stored state unchanged, and a refusal is never answered by a headerless retry. (3) Persistence: session-bound storage, hydrated and validated before the first dispatch; missing legacy state becomes normal, corrupt state is a visible reset, a late restore cannot overwrite a newer command, and a failed save is reported rather than promised. (4) Semantics: the value applies to subsequent requests for every thread in the session, snapshotted with the thread id; no thread rotation and no reprioritising queued or running work. A boost stays for the session and is shown on resume and in the context view.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Verified on main: ServerApp parses the header and silently ignores a malformed value, so criterion 4 cannot be met extension-side alone and the refusal is a wire change. Plan review landed 2026-11-26 (external pro tier); its findings are accepted into the plan and criteria: send an explicit normal rather than omit the header, refuse before any effect inside the existing envelope, make the session value authoritative at final assembly, and settle persistence scope, ordering and thread-update semantics. The branch is two deliverables; authentication, caps, expiry and per-thread controls are excluded.
<!-- SECTION:NOTES:END -->
