---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T20:44:00Z'
labels: []
dependencies: []
priority: high
ordinal: 177000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Today every request from the Pi extension looks identical in the queue, so a long speculative run and the interactive turn you are watching compete equally: the one that should win can lose. Two additions: the extension sends a priority on every request, and a /priority command sets normal, high or low for the session, default normal, persisted across restarts. The router parses the header on the inference path and refuses a malformed value instead of silently defaulting. Priority changes only the order work is dispatched in: no lease or capacity rule changes, no new authority, and a thread's priority cannot be raised by unregistered traffic. Follow-on priority scheduling (task 103) builds on this.
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
- [ ] #8 -p given an empty, dash-leading or missing value is refused, a repeated -p is refused, and omitting -p keeps the default.
- [ ] #9 The admin thread route refuses a supplied priority it cannot read exactly — non-object, unknown class, or a sub that is not an integer within 0-255 — in the existing 400 envelope.
- [ ] #10 A CLI test counts raw request arrivals so a malformed body cannot slip past the no-request assertion.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two halves, one deliverable. Router: parse the priority header with the grammar the admin thread route accepts; an absent header is the configured default; a repeated header is refused; anything malformed is 400 invalid_priority in the existing envelope, before any queue, lease, ticket or stream state exists. Extension: a session-scoped value defaulting to normal, sent on both request paths, with /priority to report, set or reject, replayed on resume. Priority says how urgent, the profile says what capacity: keep them separate. Any new refusal reason goes through wire review first.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Router half on pull request #86, revision entry v0.12.162: 0406520, ingress fix f3c08eb, main merge 4c75ffa, CLI and SPEC fix e900a48, then a12012b for the second review's two findings — the CLI treating an unusable option value as absent, and the admin route coercing a supplied sub with a cast and a clamp. My filtered pass at the working tree was 199/199 and PR CI is green at a12012b. Wake: the commit review of 4c75ffa..a12012b landing from the external service, then a merge commit onto main with CI green there. Accepted gaps: aborted or chunked upload precedence is unpinned; criterion 3 waits on the extension status view; and the admin surface now answers 400 where it used to answer 204 for an unreadable priority body, which is recorded as the wire change it is. The client half lives in the live extension file on the router host, which is not under version control, so the divergence risk sits with task 186.
<!-- SECTION:NOTES:END -->
