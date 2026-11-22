---
id: TASK-156
title: Publish applied catalogs and honor exact inference profiles
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-22T02:14:00Z'
updated_date: '2026-11-22T02:41:00Z'
labels: []
dependencies: []
priority: high
ordinal: 151000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Expose the applied model catalog and support a named profile on each inference request for the Pi integration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Cold choices, aliases and named slots/windows are visible from one safe read without launch details.
- [x] #2 No-op and refused reloads emit no catalog change. Instance restart invalidates the earlier tag.
- [x] #3 A request can select a smaller exact profile after other uses drain. Incompatible, stale or unknown choices fail without an incorrect dispatch.
- [ ] #4 Independent review of catalog publication and final dispatch is resolved and the change lands on main.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Read one applied generation with instance and conditional ETag. Emit catalog.changed after changed publication. Resolve X-Inference-Profile against the original request model, validate capacity, and carry through queued engine selection, preparation and readiness. Qualify concurrent callers, warm and cold profiles and reload.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Applied-catalog and exact-profile checks passed locally. Follow-up review identified equal-window slot identity, admin drain and the verified transport submission boundary. The fixes include original-ticket and no-checkout assertions. [Backend implementation and review corrections](https://github.com/ArborealML/Inference-Router/pull/77).
<!-- SECTION:NOTES:END -->
