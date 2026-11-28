---
id: TASK-184
title: Send only the required context from Pi; never a serving profile
status: Waiting
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-25T07:52:00Z'
updated_date: '2026-11-27'
labels: []
milestone: m-16
dependencies:
  - TASK-183
priority: high
ordinal: 176000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-183 lands numeric ingress compatibility. Pi retains a conversation/compaction ceiling separate from any intentional hard minimum and actual request fit. Remove bundled profile selection without forcing every small request to reserve the full conversation ceiling.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Both outbound paths send no named/preferred profile after migration; intentional numeric floor is preserved.
- [ ] #2 Automatic mode sends actual request demand or lets AIR compute it; selecting a 1M conversation ceiling does not impose a 1M floor on a small request.
- [ ] #3 Picker shows numeric context and qualified eligible potential, not recipe/lane selection; resident allocation is separate.
- [ ] #4 Impossible explicit floor gets router refusal; reduced eligible context warns/refuses or compacts privately under policy.
- [ ] #5 Turn capture, restore, compaction, auxiliary calls and priority survive; test pinned Pi version and final outbound bytes.
- [ ] #6 SPEC §§6 and 13 document compatibility and cutover; server retires header only after supported installed clients migrate.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Reuse catalog/session controls; distinguish selected conversation ceiling from hard floor. Preserve explicit third-party minima. Consume eligibility-filtered catalog when policy lands. Coordinate TASK-188 provenance.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Old plan sent the selected window on every call, defeating dynamic allocation. Installed migration remains unqualified.
<!-- SECTION:NOTES:END -->
