---
id: TASK-208
title: Linearize sensitivity escalation and human declassification
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T12:31:00Z'
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
Use the verified owner and policy-group store from TASK-207. Add durable escalation and revision-bound lowering. Expose lowering only when the external authority explicitly supplies the separate human privilege for that group, revision and target. Retried escalation is idempotent; concurrent stale lowering fails. Preserve restrictions across restore and derived sessions.

Order policy publication with the last reversible dispatch decision for the same group. Bind a one-use authorization to the exact provider, body, attempt, configuration and policy revision. Persist policy before acknowledging escalation. If earlier authorization already permits transmission, record possible exposure and request cancellation; keep its physical and financial obligations until reconciliation. Keep file, authority and network I/O outside scheduling gates. An unrelated group can progress.

For private transport validate trust and revision before every attempt, including retries and auxiliary requests. Exercise the commercial side with controlled permit and liability receipts only. Race delayed persistence and send in each order. Crash or an uncertain external write must deny new authorizations until reconciled. Check status and Pi-facing responses disclose the effective scope and revision. Paid sending and external credential issuance retain their existing owners.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The revision and final-send plan is under independent review against the private-policy candidate. Platform authority for real lowering remains separately gated.
<!-- SECTION:NOTES:END -->
