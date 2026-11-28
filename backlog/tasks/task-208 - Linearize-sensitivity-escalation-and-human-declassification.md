---
id: TASK-208
title: "Linearize sensitivity escalation and human declassification"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-207
priority: high
ordinal: 208000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Allow an agent to escalate autonomously and require genuine owner authorization to lower sensitivity. Keep policy commit and final send authorization ordered, without holding queue gates across I/O. Design and fake qualification can start before choosing the production confirmation boundary.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Escalation is persisted before acknowledgment; if it wins the send race no old-policy send is authorized, including delayed transport, preparation and parallel tools.
- [ ] #2 If send authorization wins first, report committed exposure and cancel where possible; never claim previously authorized bytes were recalled.
- [ ] #3 De-escalation requires owner-only authority bound to lineage, current revision, target and transcript scope; stale approvals fail compare-and-set.
- [ ] #4 Agent-accessible bearer, tool boolean, slash command or new thread cannot authorize de-escalation; negative capture tests cover retries and derived sessions.
- [ ] #5 SPEC §§6, 13–14 state supported threat model, user confirmation and distinction from arbitrary agent tool/network containment.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Bind the final send to immutable attempt/body/config and policy revision. Use a separate trusted owner credential or confirmation channel inaccessible to the agent; fake both race orders first. Document actual production boundary before enabling commercial egress (TASK-217).
<!-- SECTION:PLAN:END -->
