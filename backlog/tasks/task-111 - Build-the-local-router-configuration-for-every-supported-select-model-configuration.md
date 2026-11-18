---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: Waiting
assignee:
  - '@qwen3.8-flash-next-board'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-18T19:18:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Wake when TASK-26 supplies native launch settings and TASK-118 verifies the exact serving target. Deliver a machine-local configuration for all supported selector combinations and demonstrate parity by dry-run and provider fixtures.
<!-- SECTION:DESCRIPTION:END -->

## Acceptance Criteria
<!-- AC:BEGIN -->
- [x] #1 Inventory models, aliases, defaults, valid profile combinations, GPU selection and utilization, parked RAM cache and parallelism, and suffix-cache behavior at an identified selector revision.
- [ ] #2 Produce a local config that passes the router config dry-run. Compare resolved targets and launch settings with the selector for every supported combination, covering boundaries for parameter ranges.
- [ ] #3 Track missing router capabilities as dependencies. Close only when every supported configuration is representable and resolves correctly, with a redacted coverage report.
<!-- AC:END -->

## Implementation Plan

<!-- SECTION:PLAN:BEGIN -->
Inventory the selector and each delegated validator from a fixed source fingerprint. Enumerate model and profile aliases, defaults, option ranges and cross-family refusals. Compare the resolved settings with the native launch request before writing local TOML. Validate the private configuration by dry-run, then compare exact payloads and refusal cases against fake providers.
<!-- SECTION:PLAN:END -->

## Implementation Notes

<!-- SECTION:NOTES:BEGIN -->
The local selector has model-specific validators and launch resource settings that the current native provider cannot represent. TASK-26 covers GPU devices, mounts and resource limits. Use the compatibility inventory to distinguish additional provider work from invalid selector combinations.

Static inventory is underway. The reference exposes nine canonical models, thirteen model aliases and twenty-three profile spellings. Context and slot counts alone cannot identify every launch preset. The scheduler currently skips an inactive engine before considering launch eligibility. TASK-114 records the cold-start gap. Validate enumerated counts independently before accepting the inventory criterion.

Independent static enumeration at selector source fingerprint 8af827d3c375efda08cad8a5b9b7fd862b44a2f5a7482042c5255623a891a030 covered 506 explicit spellings: 169 pass structural validation and 337 fail. The 75 distinct targets include 12 accepted spelling combinations requiring an explicit GPU. All 22 omitted-profile cases matched their defaults. Parking exposes 1,248 RAM and parallelism combinations. No runtime trial was part of this inventory.

Target-proof plan dispatched on 2026-11-18. Static controls: single-device profiles require one explicit index; paired profiles require two distinct indices. GPU utilization accepts 0.50–0.95 inclusive, default 0.90. Parking RAM accepts 1–48 GiB, default 32; variable lanes accept 1–8. This revision has no supported suffix-cache launch mechanism. Controls cover malformed, out-of-range and unsupported options.
<!-- SECTION:NOTES:END -->
