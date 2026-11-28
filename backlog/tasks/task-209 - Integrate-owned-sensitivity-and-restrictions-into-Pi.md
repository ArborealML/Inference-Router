---
id: TASK-209
title: "Integrate owned sensitivity and restrictions into Pi"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-184
  - TASK-207
  - TASK-208
priority: high
ordinal: 209000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Pi remains an AIR client. Associate ordinary and auxiliary calls with the externally established session/policy group, expose effective sensitivity and restrictions, and request escalation/de-escalation through AIR's minimal policy API. No agent sandbox, identity issuer or client route scorer.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Ordinary and auxiliary requests, fork/restore/compaction carry authorized group identity and restrictions; supported Pi integration cannot bypass AIR selection.
- [ ] #2 Escalation waits for durable AIR acknowledgment before dependent inference; failed escalation blocks dependent inference.
- [ ] #3 Pi displays effective sensitivity and affected transcript/group scope; de-escalation uses externally authorized privileged operation, never an inference tool credential.
- [ ] #4 Eligibility-filtered catalog controls conversation ceiling; warn/refuse or compact privately when eligibility shrinks.
- [ ] #5 Pinned Pi API and outbound hook fixtures cover both paths, retries and restore; TASK-186/188 safety and installed parity gate production Pi release.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Consume 183/184's migrated numeric API and 207/208's policy contract. Test fake external identity/privilege, no sandbox implementation. The client/orchestrator supplies session associations; AIR validates them. Keep de-escalation disabled until externally authorized. Coordinate existing TASK-186/188; do not require live Pi rollout to qualify direct non-Pi inference.
<!-- SECTION:PLAN:END -->
