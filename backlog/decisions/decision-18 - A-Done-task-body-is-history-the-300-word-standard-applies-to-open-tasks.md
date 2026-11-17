---
id: decision-18
title: >-
  A Done task body is history: the 300-word standard applies to open tasks
date: '2026-11-17T12:05:00Z'
status: accepted
---
## Context

The owner's standard is that a task is human readable and not verbose, and the board checker enforces a
300-word Description. It currently counts every file, so eleven closed tasks report defects forever and the
board's defect count stops meaning anything: 15 of them today, only four of which are work. That makes the next
reader think the board is failing when it is holding history.

## Decision

**The 300-word line applies to non-Done tasks.** A Done task's Description is the record of what was asked at the
time; it is not rewritten to satisfy a limit, and its length is not a defect. The checker should filter on status
(reported as: `WORDS (non-Done)` plus, separately, a count of over-length Done tasks as information).

## Consequences

Open tasks over the line are real work and are tracked on TASK-47: TASK-11, 14, 23, 28. Closed tasks keep their
prose, which is where several dispositions are only written down once. If the checker is not changed, the board
must keep quoting the two numbers separately — total defects, and open defects — so the gate stays honest without
pretending history is a defect.

**Addendum, recorded so the exemption is named rather than implied (owner question at b214ce6).** The over-length
Done bodies are exactly nine tasks — TASK-7, 10, 20, 24, 27, 36, 37, 40, 41 — and they are history, not deferred
work: each carries a disposition that exists nowhere else. Trimming them would delete the record the owner asked
for, so they stay, and the checker reports them as an informational count, never as a defect. The open-task limit
still bites: at b214ce6 the board measures 0 WORDS defects across 47 task files.
