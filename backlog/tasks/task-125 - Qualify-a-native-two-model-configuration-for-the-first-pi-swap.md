---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Waiting
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-20T00:11:00Z'
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
Waiting for independent feature review and hosted CI on [PR #37](https://github.com/ArborealML/Inference-Router/pull/37). The results wake the delivery manager. The DeepSeek 32k and Qwen 256k-4 offline swap fixture passed six of six Release cases with zero skips at 73dce89. Both dedicated configuration loads passed at 1d97c2f with identical runtime, CLI and input content carried forward. Live Pi qualification still requires enrolled multi-engine topology, host fencing and verified dispatch.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Retain six focused rows and two config-only reads. In each direction freeze the request, validate the offer and use certified send; observe exact delivery on the intended stream and zero bytes on the alternate. Distinguish image and model mismatch from a current measured window below the requirement. Check same-engine stale issuer rejection at checkout and first write. Pin DeepSeek inference and readiness paths and probe fields with fakes. Keep the multi-engine refusal intact.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Accepted the f6e59a8 feature review and d022344 assertion correction. Each direction starts with a fresh incumbent. Image, model and tier substitutions give zero new delivery. The fixture independently hashes reference launch settings and shares the final checkpoint digest routine with production. The existing closed-route staging test owns the held-grant first-write refusal. Neither warrants another pair test. Fixture publication and issuance supply offline evidence only. Historical operator-selected Qwen lacks an authenticated applied launch. Live qualification requires original launch provenance and closed-channel observation of the exact serving process.
<!-- SECTION:NOTES:END -->
