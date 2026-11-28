---
id: TASK-208
title: Linearize sensitivity escalation and human declassification
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-27'
updated_date: '2026-11-28T13:22:00Z'
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
Use per-group durable revisions and a short authorization gate. Authenticate and deduplicate escalation, fence new authorizations, capture unresolved consumed attempts and request cancellation outside the gate. Keep confirmed and proposed snapshots until durable storage and the external head confirm the same operation and predecessor. Acknowledge only after publication. A slow group cannot serialize independent groups’ intent writes. Loss of global authority closes all grants.

Persist an owner-bound attempt intent before transmission. At the last reversible HTTP handoff, validate and consume once under the group gate and register possible exposure. Bind exact projected bytes, destination, scope, writer, trust, credential and policy revisions. A prior fence means zero bytes. Prior consumption means possible exposure even if the actual write comes later. Require fresh authorization for retries and request-derived probes. Reconcile ambiguous writes, cancellation and physical settlement separately across restart. Hold possible paid liability until separately settled. Use only controlled commercial receipts here.

Keep lowering disabled without independent human privilege bound to owner, group, expected revision, operation and target. Historical mutation retries return the previous outcome and current effective policy. Qualify both authorization orders on real loopback HTTP, buffered and streamed, across delayed persistence, lost head confirmation, restart and independent-group progress. Report attempt and current policy separately; Pi owns client presentation.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Review of 461b3c7 found a check-before-send gap and one global store writer. Qualify group-local recovery, the final transmission handoff and separately authorized probe traffic. Actual paid egress and human lowering retain their existing owners.

The private escalation and final-send implementation is under review in [PR 102](https://github.com/ArborealML/Inference-Router/pull/102). On the source candidate, 164 focused tests passed. A separately composed Native AOT host served private inference, escalated sensitivity and returned the confirmed policy revision. Lowering and live deployment remain open.

Review of 0c1f05a identified shared engine admission retained across private journal writes. Exercise two groups on one engine during intent creation and retirement. Verify exact content and projected headers at handoff and report exhausted unresolved-intent capacity.
<!-- SECTION:NOTES:END -->
