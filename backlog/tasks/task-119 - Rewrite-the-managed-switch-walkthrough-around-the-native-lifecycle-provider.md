---
id: TASK-119
title: Rewrite the managed switch walkthrough around the native lifecycle provider
status: Done
assignee:
  - '@gpt-6.2-sol-lifecycle'
created_date: '2026-11-18T19:04:00Z'
updated_date: '2026-11-18T20:03:00Z'
labels: []
milestone: m-4
dependencies: []
references:
  - SPEC.md
modified_files:
  - SPEC.md
  - samples/router.toml
ordinal: 117000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
The managed switch walkthrough leads with the native Docker provider. The legacy selector, shipped limitations and target recovery behavior are documented separately.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 SPEC sections 5.3 and 5.3.1 lead with native provider selection, preserve the legacy exec contract, and describe the existing seam and actual verification limits.
- [x] #2 The auto-switch comment and catalog and profile examples at SPEC.md:1996, 2024–2042 and 2079–2088 present a coherent managed configuration. Every remaining select-exec example identifies the legacy path.
- [x] #3 The transaction discussion at SPEC.md:1499–1510 distinguishes target quarantine from independent recovery. Explain the outage between incumbent stop and verified alternate readiness on a single host, plus behavior under uncertain occupancy.
- [x] #4 Validate the primary example with the offline config loader. Cross-check ownership, host-exclusivity and recovery claims against the implementation and distinguish documented requirements from shipped behavior.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace the managed switch examples to the config loader and provider. Distinguish proven behavior from the host fencing and independent recovery requirements. Submit the proposed walkthrough for review, validate the example offline, and cross-check each cited section before review handoff.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Integrated in [05fe144](https://github.com/ArborealML/Inference-Router/commit/05fe144). The coordinator extracted the complete native sample verbatim from SPEC §7, loaded it offline, and verified both models and all three named serving profiles. The shipped reference TOML also passed the loader. The independent review confirmed the provider and readiness boundaries. Two follow-ups corrected the executable and flags throughout the CLI synopsis and explained how an occupied host port can surface after the owned incumbent stops. No live lifecycle operation was part of this verification.
<!-- SECTION:NOTES:END -->
