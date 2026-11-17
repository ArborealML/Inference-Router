---
id: TASK-35
title: >-
  Close the no-capacity parity gap: a request that nothing can ever serve should
  be refused promptly
status: To Do
assignee:
  - '@qwen3.8-flash-next'
created_date: '2026-11-17T08:16:00Z'
updated_date: '2026-11-17T08:17:00Z'
labels:
  - wire
  - scheduling
dependencies: []
priority: medium
ordinal: 34000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Carrier for a parity gap that was hiding as a stray checkbox. TASK-10 carried an old criterion - a prompt no-capacity error when every engine is unhealthy - that was never implemented and was deleted from TASK-10 as a stale duplicate rather than ticked. Verified current behaviour, not assumed: the scheduler answers a request with no eligible engine as Wait("no_engines") (PrefixAwareScheduler.cs:59), so the request queues until a ceiling expires and the caller is answered 503 through model_unavailable or switch_failed (ServerApp.cs:306). So the behaviour today is wait-then-refuse, and the gap is whether a request that nothing can ever serve should be refused at once.

What each option changes: **fast-refuse** - a caller learns immediately that no engine can take the model, which is what an agent wants, at the cost of failing requests that would have been served by an engine arriving one second later; **keep waiting** - maximises the chance of eventual service and keeps today's wire behaviour, at the cost of a caller spending its whole patience on a hopeless request; **accept the gap** - say in SPEC 14.1 and 15 that wait-then-503 is the intended posture, which is a legitimate answer and better than silence.

No new wire shape is needed for any of the three: 503 model_unavailable already exists, so this is a timing and specificity question. Implementation and the SPEC disposition are the coding session's; the board records whichever way it goes. TASK-10 AC #9 already requires every remaining parity gap to have a named carrier - this is that carrier, so closing it without a disposition would reopen AC #9.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One of fast-refuse, keep-waiting, or accepted-gap is chosen, and SPEC 14.1 row for it says which, with the reason
<!-- AC:END -->
