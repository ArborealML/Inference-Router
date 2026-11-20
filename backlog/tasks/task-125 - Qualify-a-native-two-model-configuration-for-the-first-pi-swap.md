---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: Waiting
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-20T00:23:00Z'
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
Waiting for enrolled topology, host fencing and authenticated serving observation for the live Pi swap. Evidence of those prerequisites wakes live qualification. [PR #37](https://github.com/ArborealML/Inference-Router/pull/37) landed the selected DeepSeek and Qwen offline proof. Two dedicated config-only inputs passed, and six bidirectional transport and refusal cases passed at 73dce891. The remaining criterion requires real host evidence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [x] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Retain six focused rows and two config-only reads. In each direction freeze the request, validate the offer and use certified send; observe exact delivery on the intended stream and zero bytes on the alternate. Distinguish image and model mismatch from a current measured window below the requirement. Check same-engine stale issuer rejection at checkout and first write. Pin DeepSeek inference and readiness paths and probe fields with fakes. Keep the multi-engine refusal intact.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Independent review accepted the exact tested head 73dce891. [Hosted CI](https://github.com/ArborealML/Inference-Router/actions/runs/44643624811) passed all three jobs. Merge ae0b3c3 preserves the reviewed implementation and fixtures. Separate configurations qualify DeepSeek 32k and Qwen 256k-4 independently. The older [Qwen and Swift fixture](https://github.com/ArborealML/Inference-Router/pull/34) retains its original scope.
<!-- SECTION:NOTES:END -->
