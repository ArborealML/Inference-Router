---
id: TASK-84
title: >-
  Decision: where the external review pass runs — hosted service, local agent
  DAG, or both
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-18T04:13:00Z'
updated_date: '2026-11-18T04:16:00Z'
labels:
  - process
milestone: m-10
dependencies: []
ordinal: 84000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Your word at 04:02Z: **"CLM is starting to annoy me compared to our dag."** The working contract makes an external review pass mandatory — every plan before code exists, every commit after it lands — and that pass runs on the hosted service, which is what produces the traffic you are reacting to. Attribution from the request log: of seven passes in the last three hours, three were mine (competitor evaluation, plan review, commit review) and four were the clerk seat's design and audit passes, which have already stopped. The contract does not let me stop unilaterally, so the route is your call.

**(A) Keep it, cut the volume.** Mandatory for plans and commits only, no design essays, lower tier for routine commits. You keep a reviewer with no shared context: the two-commit review is what found the stale-window race now sitting on the top correctness row, found independently. Cost: about one notification per landing.

**(B) Move reviews to the local agent DAG.** Faster, free, silent. The reviewer shares this seat's context and model family, so it re-reads the same reasoning and the independent-catch class largely disappears. The contract's review rule and a decision entry change.

**(C) Hybrid — my recommendation.** The DAG does plan attacks and routine commit review continuously; the hosted service keeps only the two highest-consequence moments: a concurrency or lifecycle plan before code, and the commit that lands it. One or two hosted passes per feature, not per commit.

**Answer needed, one line: A, B or C.** It unblocks the plan review for the stale-window fix, which is parked here. Two minutes. Nothing else in your in-tray blocks: commit identity is settled and in force, and the optional bot account so commits link to an author waits on nothing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 One of A, B or C is chosen, in the row or in a reply
If B or C, the working contract's review rule and a decision entry are updated in the same landing, naming what the DAG reviewer may and may not replace
The choice names which passes stay hosted, so a future seat does not guess
<!-- AC:END -->
