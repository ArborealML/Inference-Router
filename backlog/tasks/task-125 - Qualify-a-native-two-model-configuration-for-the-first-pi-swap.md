---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T15:41:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-118
  - TASK-26
priority: high
ordinal: 123000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Config is preparing the offline Qwen and Swift pair evidence. Native operation awaits a trusted launch record and both readiness reads bound to the same serving process. The historical operator-launched Qwen instance has no participating controller journal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reconcile the existing config-only receipts and frozen target identities against the landed projection. Identify which bidirectional fake-provider assertions already cover this pair and which are missing, including wrong image, model and profile. Return the evidence map and a bounded plan for any uncovered assertion. No live host action.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Both models served Pi turns through AIR during operator selector transitions. The last saved Qwen 256k-4 observation reported 256,000 context and four slots. Current residency remains unverified. Config and the resolver supply native intent, the selector supplies historical logs and artifact receipts, Docker supplied historical container/image identity, and Strata supplied direct roster and capacity. None binds the applied operation, epoch and full plan to the current serving process. A future authorized launch must record the original operation, epoch, frozen plan and immutable instance at launch time. An observation-only issuer must attest the connected peer and bind both replies to the same serving process while inference admission is closed. TASK-112 retains the native client-driven swap proof.
<!-- SECTION:NOTES:END -->
