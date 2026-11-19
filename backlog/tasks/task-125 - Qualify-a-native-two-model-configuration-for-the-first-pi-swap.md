---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Waiting
assignee:
  - '@chatswarm'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T15:46:00Z'
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
The bounded offline pair plan review dispatched November 19 on 5047f8f wakes the coordinator. Scope is configuration-only validation of the existing resolved pair and fake-provider evidence for explicit Qwen 256k-4 and Swift max. A native swap still awaits a trusted launch record and independently observed readiness bound to the serving instance. The manual Qwen launch had no participating controller journal.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Validate the hash-pinned pair using the config verb once. Bind the redacted pair to current create digests and distinguish the stale pre-port projection. Seed each direction independently through the staged coordinator. Observe one exact send after matching Open and zero delivery for wrong image-backed digest, wrong model and incompatible valid tier. Preserve the applied-launch recovery guard. The two fake directions provide no serial same-controller or live-swap evidence. Reuse generic controls and the existing selector sweep.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Both models served Pi turns through AIR during operator selector transitions. The last saved Qwen 256k-4 observation reported 256,000 context and four slots. Current residency remains unverified. Config and the resolver supply native intent, the selector supplies historical logs and artifact receipts, Docker supplied historical container/image identity, and Strata supplied direct roster and capacity. None binds the applied operation, epoch and full plan to the current serving process. A future authorized launch must record the original operation, epoch, frozen plan and immutable instance at launch time. An observation-only issuer must attest the connected peer and bind both replies to the same serving process while inference admission is closed. TASK-112 retains the native client-driven swap proof.
<!-- SECTION:NOTES:END -->
