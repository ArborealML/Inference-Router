---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T23:53:00Z'
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
Config is implementing the independently approved offline pair plan on 0977a26 (SPEC .97). DeepSeek V4.1 Flash 1017 IQ2-XXS uses 32k (32768 context, one slot); Qwen 3.8 Flash Next IQ3_S uses 256k-4 (256000 context, four slots). The revised plan passed review on 98db2b2. In the stale checkout control, the earlier transfer counts, while only the current issuer authorizes first write and its stream receives the payload. Six fake cases and two dedicated config loads await qualification. A live Pi swap still requires enrolled multi-engine topology, host fencing and verified dispatch.
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
