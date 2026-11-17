---
id: decision-7
title: 'The event feed records a terminal outcome before it wakes the waiter that outcome ends'
date: '2026-11-17T05:48:00Z'
status: accepted
---
## Context

A switch that failed completed its shared readiness task first and appended
`engine.switch_failed` afterwards, and ticket completion resolved the waiter before its own reason was
recorded. Anything woken by that ordering — a retrying scheduler, a ticket-scoped reader, an agent
watching its own request — could observe the failure and then look for its cause in a feed that had not
written it yet. Replay order is the only contract a late reader has, so an outcome that arrives after its
wake is a hole the reader cannot close. Shipped in v0.12.3 (`0039b1e`); SPEC §10.

## Decision

Options considered:

1. **Leave the ordering** and tell readers to re-poll until the cause appears.
2. **Record the terminal outcome, then wake the waiter that outcome ends** — append
   `engine.switch_failed` / `engine.switch_completed` and the request's terminal event before completing
   the readiness task or resolving the ticket.
3. **Give every event a causal id** and let readers reconstruct order. Rejected as a wire addition that
   does not fix the race, only describes it.

**Chosen: 2.** The feed is append-then-notify, never notify-then-append.

## Consequences

- A reader that reconnects and replays sees a failure and its cause together, in cause-first order; a
  test asserts `engine.switch_failed` reaches the feed before the `request.failed` it causes.
- Nothing in the wire shape changed: same event kinds, same casing, a guarantee about their order.
- Cost is a narrow ordering obligation inside the runtime: any new terminal path must record before it
  wakes, which is the kind of invariant that wants a test per path rather than a comment.
- Loss reporting and the stream epoch are separate and still unbuilt: this decision fixes *ordering*, not
  the inability to say how many events a slow consumer missed (awaiting the live-view decision, TASK-21).
