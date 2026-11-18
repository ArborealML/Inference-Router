---
id: decision-32
title: >-
  An answered owner decision is not re-asked: the decision list is the answer
  of record
date: '2026-11-18T04:32:00Z'
status: accepted
updated_date: '2026-11-18T04:32:00Z'
---
## Context

The commit-identity question was answered by the owner and recorded as decision-29, with the two identities and
the per-commit mechanism written into the working contract an hour later. It came back anyway: a neighbouring
seat reported the row as waiting on the owner, and this seat's own handoff had to be corrected. Asked directly,
the owner said the annoyance was context management losing facts, not the review service — and losing the
answered question is exactly what reopened it.

## Decision

An accepted decision is the answer of record. Before anything goes to the owner as a question, a seat reads
`backlog decision list` and the task's own text, and cites the decision instead of reopening it. When a seat
compacts its context, what survives the compaction is the settled decisions, the decisions genuinely still
waiting on the owner, and the exact numbers still in play.

## Consequences

AGENTS.md Workflow §4 states the rule. A re-ask is named as a context-management defect rather than repeated
politely, and the owner's in-tray holds only items nobody has decided. Each seat owns its own compaction
discipline: the shared record exists so that no seat has to remember anything to act correctly.

## Alternatives

Re-state an answered question as a reminder (rejected: the owner pays for the same decision twice). Keep the
answer in a seat-local note (rejected: it does not outlive the session, and the decision file is the record
every seat can read).
