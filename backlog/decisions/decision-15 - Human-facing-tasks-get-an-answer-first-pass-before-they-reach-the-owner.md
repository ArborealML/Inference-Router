---
id: decision-15
title: Human-facing tasks get an answer-first pass before they reach the owner
date: '2026-11-17T09:46:00Z'
status: accepted
---
## Context

The owner asked twice for human-facing instructions to be specific, short and linked - "human readable, but not overly
verbose", "include specific instructions with links where relevant so that the user can get in, do the task and get you
an answer as quickly as possible" - and asked for a multi-pass strategy with subagents allowed. Nothing on the board
recorded that instruction, so it was applied inconsistently: the same morning produced tasks with the answer at the top
and tasks with 677 words of reasoning before the request, nine links pointing at the tasks **folder** rather than a task
file, and one decision task about publishing a release containing zero links.

## Decision

Every task assigned to a person gets an **answer-first pass** before it counts as ready, in this order:

1. **Answer block first** — the one line of answer wanted, the exact command or settings path, the minutes it costs.
2. **Options as one line each**, with the cost of each, and a recommendation marked as a recommendation.
3. **Links that resolve on a specific file**, never a folder; commands in fenced blocks the person can paste.
4. **Reasoning moved below or into Implementation Notes**, so the description stays under roughly 300 words.
5. **A check pass by a second reader** (a subagent or the external service) that answers the task as written without
   asking a follow-up question; if it cannot, the task is not ready for the owner's list.

## Consequences

- Descriptions already written before this rule keep their reasoning, but each now opens with the answer block; the
  tails still need trimming to the 300-word target and that is recorded as remaining work rather than claimed as done.
- Folder links are a defect, not a style choice: a link that lands on a 30-file directory makes the owner do the
  indexing the agent was supposed to do.
- The pass is cheap and mechanical enough to run on every owner task, which is what makes a queue of one-line answers
  possible at all.
