---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T16:10:00Z'
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
Config owns a fixture analyzer correction at f6e59a8. Focused Release qualification stopped before test discovery on xUnit2000: the constant expected image ID must be the first inequality argument. Independent review of the two-file fixture diff was dispatched November 19. The resolved pair passed config-only validation at 5047f8f. Directional fake delivery and live host authority remain open.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Audit private versus redacted selected settings under an explicit role mapping. Compare independently derived typed checkpoint digests with each resolved target. Use two separately seeded directions, preserve exact pending applied launches, and bind the intended target digest at staged acceptance, checkout and first write. Observe exact bytes only after matching Open. Exercise an internally consistent wrong-image digest, wrong model, valid Swift max-2 and stale pre-port evidence. Separate fixture enforcement from production host authority. Run only the pair fixture; retain the existing config readback.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Both models served Pi turns through AIR during operator selector transitions. The last saved Qwen 256k-4 observation reported 256,000 context and four slots. Current residency remains unverified. Config and the resolver supply native intent, the selector supplies historical logs and artifact receipts, Docker supplied historical container/image identity, and Strata supplied direct roster and capacity. None binds the applied operation, epoch and full plan to the current serving process. A future authorized launch must record the original operation, epoch, frozen plan and immutable instance at launch time. An observation-only issuer must attest the connected peer and bind both replies to the same serving process while inference admission is closed. TASK-112 retains the native client-driven swap proof.
<!-- SECTION:NOTES:END -->
