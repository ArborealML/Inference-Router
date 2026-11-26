---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T09:59:00Z'
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
- [ ] #4 A rejected priority value produces the router's stated refusal, not a silent default.
- [x] #5 A malformed priority is refused before any scheduler mutation, dispatch or stream start, in the existing error envelope.
- [x] #6 The session value is the one present on the final outbound request on both request paths and after a session restore.
- [ ] #7 air threads refuses a malformed -p value with a non-zero exit and registers nothing.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two halves, one deliverable. Router: parse the priority header with the grammar /admin/threads already accepts; an empty value is unset; a repeated header is refused; anything malformed is 400 invalid_priority in the existing envelope, before any queue, lease, ticket or stream state exists. Extension: a session-scoped value defaulting to normal, sent on both request paths, with /priority to report, set or reject. No scoring or lease behaviour in this commit. A restored session must re-assert its priority before completing a turn, or its first request silently takes the default. Priority says how urgent, the profile says what capacity: keep them separate. Any new refusal reason goes through wire review first.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Router half on pull request #86: 0406520 plus fix f3c08eb, main merged at 4c75ffa, revision log renumbered to v0.12.162. The fix moves the priority header check ahead of the body read, pins precedence against oversized and malformed bodies over real HTTP, and restricts the grammar to an exact lowercase class with an optional colon and one to three ASCII digits, no sign, space or leading zero, sub at most 255. My checks at f3c08eb: build clean, 58/58 over the priority, wire and thread filters. The follow-up review over 0406520..f3c08eb found one new defect, verified in the code: the CLI's ParsePriority discards the parse result, so a malformed -p value posts the normal class with sub 0, gets 204, prints registered and exits zero, where the old lenient parser registered what the caller asked for. Two accepted gaps for this branch: section 6 omits the lexical restrictions the parser now enforces, and aborted or chunked upload precedence is unpinned now that a refusal can win an abort that used to land during the body read. Persistence is settled: admin threads are in memory and config threads carry class and sub separately. The client half landed in the live extension file on the router host, which is not under version control, so the divergence risk sits with task 186.
<!-- SECTION:NOTES:END -->
