---
id: TASK-180
title: Carry thread cache affinity to local managed engines
status: Preparing
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-24T18:57:00Z'
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
- [ ] #1 Same named thread on the same managed engine receives a stable bounded opaque identifier on normal and streaming inference.
- [ ] #2 Different threads and engines produce distinct hints. Anonymous and external requests carry no hint.
- [ ] #3 Unrelated caller headers and display metadata stay private, and the request body is preserved.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Use the existing centralized completion-request construction and local ownership predicate. Derive a domain-separated digest from engine and thread, and cover HTTP forwarding and compatibility with focused tests.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan review found that default HTTP redirects and proxies can carry a custom header beyond the selected loopback endpoint. Use a direct transport for eligible engines. Keep the verified transport header allowlist intact because verified dispatch excludes local lifecycle groups.
<!-- SECTION:NOTES:END -->
