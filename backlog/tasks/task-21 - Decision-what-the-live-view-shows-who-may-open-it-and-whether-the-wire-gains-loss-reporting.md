---
id: TASK-21
title: Document the adopted operator dashboard and feed contract
status: Done
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-17T02:18:00Z'
updated_date: '2026-11-20T23:19:00Z'
labels:
  - ui
  - wire
milestone: m-11
dependencies: []
priority: medium
ordinal: 21000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
SPEC v0.12.117 documents the operator dashboard audience, access posture, existing admin reads and retained-event feed. [PR #67](https://github.com/ArborealML/Inference-Router/pull/67) landed at 1b2c17f. All three [post-merge checks](https://github.com/ArborealML/Inference-Router/actions/runs/44835126267) passed.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 The audience and the credential posture are stated
- [x] #2 The loss-reporting event kind is approved or declined, with a one-line reason
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
An operator bootstraps current state from admin reads and follows retained events. Loopback binding and a tunnel are the recommended deployment. The listener is configurable, and reload and reset have distinct credential rules. Subscribers detect missing events from sequence gaps and the exclusive replay cursor because loss depends on the subscriber. The HTTP feed buffer holds 256 entries. Dashboard implementation is tracked separately.
<!-- SECTION:NOTES:END -->

## Comments

<!-- COMMENTS:BEGIN -->
<!-- COMMENTS:END -->
