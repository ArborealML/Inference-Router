---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T16:22:00Z'
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
Waiting for the administrator identification in TASK-137 and subsequent host enrollment. [PR #34](https://github.com/ArborealML/Inference-Router/pull/34) landed the offline Qwen 256k-4 and Swift max pair. The exact private configuration passed the config verb at 5047f8f. Five focused Release cases passed at d022344 and hosted checks passed. [Selector coverage](../../docs/selector-launch-coverage.md) records the redacted inputs and resource mapping. The reference create records preserve the effective command and selected settings for both models.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [x] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Audit private versus redacted selected settings under an explicit role mapping. Compare independently derived typed checkpoint digests with each resolved target. Use two separately seeded directions, preserve exact pending applied launches, and bind the intended target digest at staged acceptance, checkout and first write. Observe exact bytes only after matching Open. Exercise an internally consistent wrong-image digest, wrong model, valid Swift max-2 and stale pre-port evidence. Separate fixture enforcement from production host authority. Run only the pair fixture; retain the existing config readback.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Accepted the f6e59a8 feature review and d022344 assertion correction. Each direction starts with a fresh incumbent. Image, model and tier substitutions give zero new delivery. The fixture independently hashes reference launch settings and shares the final checkpoint digest routine with production. The existing closed-route staging test owns the held-grant first-write refusal. Neither warrants another pair test. Fixture publication and issuance supply offline evidence only. Historical operator-selected Qwen lacks an authenticated applied launch. Live qualification requires original launch provenance and closed-channel observation of the exact serving process.
<!-- SECTION:NOTES:END -->
