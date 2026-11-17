---
id: decision-17
title: The topic event inbox is deferred until a named trigger
date: '2026-11-17T10:48:00Z'
status: accepted
---
## Context

TASK-24 asked whether outside facts (first candidate: a merged pull request) should wake agent sessions
through a durable topic inbox. The external design answer describes a narrow one, not a broker: an append-only
store outside the repository (a SQLite file in durable user state), one poller per outside fact, a publisher
registry so an event carries an authenticated publisher id, per-event acknowledgement with leases so a crash
between waking and acknowledging replays, retention until every live subscription has acknowledged. Its own
decisive caveat, which it states itself: per-publisher tokens do not isolate agents when every session on this
machine runs as the **same OS user** — real isolation needs a separate service account, and that is the part
that turns a script into an installation.

## Decision

**Defer.** Nothing is built. Revisit when one of these is true, not before:

1. a second, independently-sourced topic is actually wanted, not merely imagined;
2. a fact has been *missed* because no existing push path covered it;
3. duplicate waking or wake-spam shows up in the ad-hoc polling that happens now;
4. the host gains real per-service isolation (a separate service account for pollers).

## Consequences

Waking stays as it is today: the research service pushes results into the session that asked, sessions message
each other, and a scheduled check can answer "what merged since I last looked" on demand. TASK-24 closes with
the deferral as its answer. If the inbox is ever built, the merged-PR slice is the agreed first source, with the
event id built from repository, PR number and merge SHA so repeated polls cannot produce two work items, and
"closed" never treated as "merged".
