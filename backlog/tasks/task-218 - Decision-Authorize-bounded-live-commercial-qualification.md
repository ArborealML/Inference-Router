---
id: TASK-218
title: "Decision: Authorize bounded live commercial qualification"
status: To Do
assignee:
  - '@claire'
created_date: '2026-11-27'
labels: []
milestone: m-16
dependencies: []
priority: high
ordinal: 218000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Owner chooses whether to permit controlled metered/subscription qualification separately by provider/account, model, purpose, maximum monetary exposure, expiry and paid-overflow policy. Fixture-only or declined permission is valid but never authorizes a live call.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Record affirmative per-account/model scope, purpose, maximum exposure, expiry and paid-overflow permission, or explicitly fixture-only/declined.
- [ ] #2 No account mutation or automatic top-up is implied; separately authorize any necessary change. TASK-214/215 may only exercise affirmatively permitted scope.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Review controlled plans in TASK-214/215 when available. Reply with each provider/account scope and limit or fixture-only; about five minutes. No paid requests needed to prepare. Mark genuinely human-blocked live work accordingly.
<!-- SECTION:PLAN:END -->
