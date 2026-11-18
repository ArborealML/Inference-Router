---
id: TASK-119
title: Rewrite the managed switch walkthrough around the native lifecycle provider
status: Ready for Review
assignee:
  - '@gpt-6.2-astra'
created_date: '2026-11-18T19:04:00Z'
updated_date: '2026-11-18T19:51:00Z'
labels: []
milestone: m-4
dependencies: []
references:
  - SPEC.md
ordinal: 117000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Make the primary operator walkthrough consistent with decisions 56 and 62. At a8db9d6 the engine declaration at SPEC.md:1358 leads into the generic exec definition at 1369–1376. The provider seam is already described at 1396–1411. Preserve that implementation description and distinguish the compatibility default at 1417 from the recommended managed configuration.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [ ] #1 SPEC sections 5.3 and 5.3.1 lead with native provider selection, preserve the legacy exec contract, and describe the existing seam and actual verification limits.
- [ ] #2 The auto-switch comment and catalog and profile examples at SPEC.md:1996, 2024–2042 and 2079–2088 present a coherent managed configuration. Every remaining select-exec example identifies the legacy path.
- [ ] #3 The transaction discussion at SPEC.md:1499–1510 distinguishes target quarantine from independent recovery. Explain the outage between incumbent stop and verified alternate readiness on a single host, plus behavior under uncertain occupancy.
- [ ] #4 Validate the primary example with the offline config loader. Cross-check ownership, host-exclusivity and recovery claims against the implementation and distinguish documented requirements from shipped behavior.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Trace the managed switch examples to the config loader and provider. Distinguish proven behavior from the host fencing and independent recovery requirements. Submit the proposed walkthrough for review, validate the example offline, and cross-check each cited section before review handoff.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
Plan review returned. The loader selects Docker whenever its table exists. Runtime checks the configured endpoint, accepts an unknown window, and does not match the profile slot count. The walkthrough will distinguish these shipped behaviors from exact instance proof, host exclusivity and target quarantine. The implementer is editing the walkthrough and will validate its verbatim example offline.

Worker commit f8b060a is under coordinator review. External commit review dispatched. Integrate the versioned revision log alongside the model-discovery entry.
<!-- SECTION:NOTES:END -->
