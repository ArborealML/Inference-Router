---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: To Do
assignee:
  - '@gpt-6.2-sol'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-18T18:19:00Z'
labels: []
dependencies:
  - TASK-26
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Deliver the machine-local configuration. Use the installed selector and its delegated model validators as the compatibility reference. Implement selection through the native provider under decision-56. Keep host credentials, endpoints and launch arguments out of tracked evidence.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The local selector has model-specific validators and launch resource settings that the current native provider cannot represent. TASK-26 covers GPU devices, mounts and resource limits. Use the compatibility inventory to distinguish additional provider work from invalid selector combinations.
<!-- SECTION:NOTES:END -->
