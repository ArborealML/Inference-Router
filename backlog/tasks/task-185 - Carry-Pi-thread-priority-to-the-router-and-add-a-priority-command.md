---
id: TASK-185
title: Carry Pi thread priority to the router and add a /priority command
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-25T07:54:00Z'
updated_date: '2026-11-26T08:57:00Z'
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
- [ ] #5 A malformed priority is refused before any scheduler mutation, dispatch or stream start, in the existing error envelope.
- [x] #6 The session value is the one present on the final outbound request on both request paths and after a session restore.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Two halves, one deliverable. Router: parse X-Inference-Priority with the grammar /admin/threads accepts; empty is unset; a valid value wins a duplicate-header vote; anything else is 400 invalid_priority in the existing envelope before any queue, lease, ticket or stream state exists. Extension: session-scoped priority defaulting to normal, sent on both request paths, /priority to report, set or reject. No scoring or lease behaviour in this commit. Persistence is a real choice — session-branch entries survive resume but not a new session, a host-side settings file survives both, or none — so criterion 3 and 5 decide it. A restored session must re-assert the priority before completing a turn, or the first request silently takes the default. Priority says how urgent, the profile says what capacity: keep them separate. Any new refusal reason goes through wire review first.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Router half 0406520, pull request #86. Review found two defects, both verified in the code: HandleInference reads and JSON-probes the body before PriorityFromHeader runs, so a malformed priority with an oversized or malformed body answers 413 or invalid_request instead of invalid_priority, against §6; and ThreadPriority.TryParse trims and uses byte.TryParse, admitting whitespace, a plus sign and leading zeros §6 does not describe. A fix commit moves the header check ahead of the body, pins both precedence pairs, restricts the grammar to invariant digits, and pins the 400 body bytes on both paths. Client half landed in the live extension /home/claire/inference-setup/pi-air/index.mjs: session-scoped value defaulting to normal, header on the single completion path at :436, /priority beside /context, resume replay through the file's own appendEntry mechanism, last entry winning. Verified by node --check, a 22-line diff read line by line, and a loopback listener printing the header per request: normal, high after the command, unchanged after a rejected argument, low after a case-folded one. Criterion 3 stays open: the value shows through the command only, not the status view. That file is not a git checkout, so the change sits in an unversioned host file a Pi update can overwrite.
<!-- SECTION:NOTES:END -->
