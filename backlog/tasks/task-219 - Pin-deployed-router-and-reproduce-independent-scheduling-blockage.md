---
id: TASK-219
title: "Pin deployed router and reproduce independent scheduling blockage"
status: To Do
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-27'
updated_date: '2026-11-28T11:08:00Z'
labels: []
milestone: m-17
dependencies: []
priority: high
ordinal: 219000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Establish deployed binary/configuration and extension provenance without exposing private details. The source explains possible blocking, but the exact installed causal path is not yet proven.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record executable commit/build digest, configuration digest and tracked versus installed Pi digest, with private artifacts held outside the repository.
- [ ] #2 Reproduce default and named thread requests with same/different models across local and remote routes; record blocker scope, queue state, readiness and upstream arrival.
- [ ] #3 Distinguish accidental shared anon cap, preferred route masking, deployment-only busy wait and actual shared caps; compare with controlled main fixtures.
- [ ] #4 Link redacted evidence and name unresolved deployment differences. TASK-188 owns installing matching Pi bytes; TASK-200 owns the non-streaming incident.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Inspect only approved artifacts and logs. Capture a redacted trace or use a safe capture backend; actual live changes require separate approval. Offline scheduler corrections can proceed while provenance is open.
<!-- SECTION:PLAN:END -->
