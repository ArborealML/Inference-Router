---
id: TASK-125
title: Qualify a native two-model configuration for the first pi swap
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T20:36:00Z'
updated_date: '2026-11-19T06:09:00Z'
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
Qualify the first two-model pair within the primary-host configuration being built under TASK-111. Resolve both native plans from local artifacts and supported profiles. Decision-68 authorizes these engine runs. Use the local pair for TASK-112 and defer peer qualification.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 The config dry-run accepts both model IDs and their chosen profiles. Report the selector revision, resolved target identities, required devices and effective launch arguments with machine-local details redacted.
- [ ] #2 Exercise A-to-B and B-to-A with a fake provider using those resolved targets. A wrong image, wrong model or incompatible profile prevents delivery.
- [ ] #3 Record host enrollment, artifact availability and how to independently observe residency and inference during the real pi test. Real mutation remains gated by host authority.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Both local models served real Pi turns through AIR across selector transitions. Offline target configuration and pure create projection are cleared for implementation with reviewed safeguards for alias/default resolution, literal environment composition, bind collisions, opt-in parsing, and refusal before mutation. TASK-112 retains the native client-driven swap proof.
<!-- SECTION:NOTES:END -->
