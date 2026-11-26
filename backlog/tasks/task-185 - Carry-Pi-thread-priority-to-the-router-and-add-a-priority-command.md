---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T10:23:00Z'
labels: []
dependencies: []
priority: high
ordinal: 177000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today every request from the Pi extension looks identical in the queue, so a long speculative run and the interactive turn you are watching compete equally: the one that should win can lose. Two additions: the extension sends a priority on every request, and a /priority command sets normal, high or low for the session, default normal, persisted across restarts. The router parses the header on the inference path and refuses a malformed value instead of silently defaulting. The priority affects only the order work is dispatched in — no lease or capacity rule changes and no new authority: a thread's priority is set on registration or by a request that carries one and cannot be raised by unregistered traffic. Follow-on priority scheduling (task 103) builds on this.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Every inference request from the extension carries a priority the router parses, including a fresh session.
- [x] #2 /priority sets the value for the session and reports it back.
- [ ] #3 The value survives a session restart and shows in the extension's status view.
- [x] #4 A rejected priority value produces the router's stated refusal, not a silent default.
- [x] #5 A malformed priority is refused before any scheduler mutation, dispatch or stream start, in the existing error envelope.
- [x] #6 The session value is the one present on the final outbound request on both request paths and after a session restore.
- [x] #7 air threads refuses a malformed -p value with a non-zero exit and registers nothing.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two halves, one deliverable. Router: parse the priority header with the grammar /admin/threads already accepts; an empty value is unset; a repeated header is refused; anything malformed is 400 invalid_priority in the existing envelope, before any queue, lease, ticket or stream state exists. Extension: a session-scoped value defaulting to normal, sent on both request paths, with /priority to report, set or reject. No scoring or lease behaviour in this commit. A restored session must re-assert its priority before completing a turn, or its first request silently takes the default. Priority says how urgent, the profile says what capacity: keep them separate. Any new refusal reason goes through wire review first.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Router half on pull request #86: 0406520, ingress fix f3c08eb, main merged at 4c75ffa, CLI and SPEC fix e900a48, all under revision entry v0.12.162. My checks at e900a48 in its worktree: build clean with no warnings, and 169/169 over the priority, thread and CLI filters. I read the diff: the lenient helper is gone, the parse runs before any request is built, exit 2 names the grammar, and air chat still forwards the raw header for the router's own 400. One comment in that diff overstates the case: the admin route does validate the class with 400 invalid_request; fix the wording next commit on this branch. Two accepted gaps: an aborted or chunked upload can now lose to an early 400 that previously won during the body read, and that precedence is unpinned; and the admin route clamps a numeric sub to 0-255 while the header refuses the same spelling, sent for review as a possible wire defect rather than patched blind. A review of 4c75ffa..e900a48 is in flight from the external service. Criterion 3 stays open: the session value shows through the command only, not the status view. The client half lives in the live extension file on the router host, which is not under version control, so the divergence risk sits with task 186.
<!-- SECTION:NOTES:END -->
