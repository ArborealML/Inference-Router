---
id: TASK-180
title: Carry thread cache affinity to local managed engines
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-24T18:57:00Z'
updated_date: '2026-11-24T19:13:00Z'
labels: []
dependencies: []
ordinal: 172000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Preserve a stable per-thread parking hint when AIR forwards inference requests to a local managed backend. The engine validates the prompt prefix before reusing computation.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Same named thread on the same managed engine receives a stable bounded opaque identifier on normal and streaming inference.
- [x] #2 Different threads and engines produce distinct hints. Anonymous and external requests carry no hint.
- [x] #3 Unrelated caller headers and display metadata stay private, and the request body is preserved.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use the existing centralized completion-request construction and local ownership predicate. Derive a domain-separated digest from engine and thread, and cover HTTP forwarding and compatibility with focused tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The [AIR change](https://github.com/ArborealML/Inference-Router/pull/84) carries a fixed per-engine digest on explicit local managed inference only. Plan review identified HTTP redirects and proxying as routes for unintended disclosure. The local transport now disables both. Commit review of 9d30a00 confirmed the wire framing, eligibility and verified transport boundary. All 37 focused Release cases passed and the two wire cases passed again with the pinned digest. Native AOT publication succeeded. The [Maya implementation](https://github.com/ArborealML/project-maya/pull/1) consumes the hint and still validates actual tokens and checkpoints. Model-backed performance qualification is separate.
<!-- SECTION:NOTES:END -->
