---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T20:36:00Z'
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
