---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Preparing
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T23:21:09Z'
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
Config is qualifying the newly selected DeepSeek V4.1 Flash 1017 and Qwen 3.8 Flash Next pair. Identify supported profiles, image and topology constraints from the installed selector. The prior Qwen/Swift bidirectional fake receipt and configuration readback remain historical evidence for that pair. Live qualification requires enrolled host authority and independent observation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Prepare the smallest offline readback of the captured targets and both handoff directions. Exercise the real topology guard, keep exact identifiers and effective settings, and report any unsupported composition before changing behavior. Keep the host untouched.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Accepted the f6e59a8 feature review and d022344 assertion correction. Each direction starts with a fresh incumbent. Image, model and tier substitutions give zero new delivery. The fixture independently hashes reference launch settings and shares the final checkpoint digest routine with production. The existing closed-route staging test owns the held-grant first-write refusal. Neither warrants another pair test. Fixture publication and issuance supply offline evidence only. Historical operator-selected Qwen lacks an authenticated applied launch. Live qualification requires original launch provenance and closed-channel observation of the exact serving process.
<!-- SECTION:NOTES:END -->
