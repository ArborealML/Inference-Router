---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-18T21:06:00Z'
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
Use the selector inventory in TASK-111 to qualify one real pair for TASK-112. Full selector parity remains required on TASK-111. Pick two loadable target artifacts and an authorized test host. Resolve each launch plan offline before granting a live test window.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Read-only peer inventory found both Ternary Bonsai 27B and Qwen3.5 2B Q4 artifacts. A prior single-lane HIP Qwen run exited 0 with all correctness and strict format checks passing. Its recorded Linux amd64 image ID and named manifest digest matched current Docker inspection. Bonsai previously completed a resident pi turn. This peer pair is a candidate for cluster qualification; it still requires native launch binding and host enrollment. The local selector inventory is separately authoritative for TASK-111.

The successful Qwen run recorded 4056 document tokens, a 32768-token server context and one lane. Its archived create settings map two AMD character devices and two read-only mounts, limit CPU to three cores, set memory and total memory-plus-swap to 16 GiB, and disable core dumps. Bonsai’s launch script uses the same resources with a different server executable and Q8 KV settings. The inspected 8192 run has no completion record. Preserve the distinction when comparing resolved create payloads.
<!-- SECTION:NOTES:END -->
