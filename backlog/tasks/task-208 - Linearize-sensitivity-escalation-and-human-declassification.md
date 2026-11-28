---
id: TASK-208
title: "Linearize sensitivity escalation and human declassification"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-19
dependencies:
  - TASK-207
priority: high
ordinal: 208000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Implement durable autonomous escalation and privileged revision-bound de-escalation plus the final send-authorization ordering. AIR verifies a scoped privilege; the external human control/sandbox layer establishes and protects it. Do not implement human authentication or sandboxing.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Persist escalation before acknowledgment; if it wins the send-authorization race, delayed transport, preparation and auxiliary inference cannot authorize old-policy egress.
- [ ] #2 Commercial authorization binds frozen attempt/body/config/provider and policy; consume once after a supplied reservation/durable-liability receipt, rechecking policy/revocation. Use fake receipts in fixtures. Private sends retain final policy/trust checks without monetary receipts.
- [ ] #3 If send authorization wins, record possible exposure and cancel if possible; no claim to recall bytes or prove non-transmission.
- [ ] #4 De-escalation requires externally supplied privileged scope bound to policy group, revision and target; stale approval fails CAS; no inference/escalation credential can lower policy.
- [ ] #5 Capture negative tests cover forged scope, retries, derived IDs, and authorization on either side of delayed persistence/send; no shared gate across I/O.
- [ ] #6 SPEC §§6, 13–14 distinguish AIR checks from external human authority and sandbox guarantees.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use one-use authorization in existing dispatch/transport, not a second scheduler. TASK-212 owns integrated commercial reservation/journal/send path; TASK-210 owns transport coverage. Test with fake privileged versus inference credentials. External control supplies genuine human approval and transcript scope; Pi displays affected context. If external lowering privilege is unavailable, keep de-escalation disabled and never advertise human-only qualification (TASK-217). Require independently authorized principal/root scope regardless. Fixture receipts confer no production egress permission.
<!-- SECTION:PLAN:END -->
