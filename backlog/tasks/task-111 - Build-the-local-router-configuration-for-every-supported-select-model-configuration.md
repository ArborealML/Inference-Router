---
id: TASK-111
title: >-
  Build the local router configuration for every supported select-model
  configuration
status: In Progress
assignee:
  - '@gpt-6.2-sol-config'
created_date: '2026-11-18T18:15:00Z'
updated_date: '2026-11-19T07:01:00Z'
labels: []
milestone: m-4
dependencies:
  - TASK-26
  - TASK-118
ordinal: 109000
---

## Description

<!-- SECTION:DESCRIPTION:BEGIN -->
Build a usable machine-local router configuration from the installed primary-host selector. Cover each valid model, alias, profile, default and supported option combination. Validate against the installed selector and router loader, and identify any missing router representation explicitly. Decision-68 authorizes local engine qualification. TASK-125 supplies the first pair while full parity proceeds.
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
Selector inventory at fingerprint 8af827d3c375efda08cad8a5b9b7fd862b44a2f5a7482042c5255623a891a030 found 169 valid and 337 invalid spellings, 75 distinct targets, 12 spellings requiring one explicit GPU, 22 omitted defaults, and 1,248 parking combinations. Single-device profiles require one explicit index; paired profiles require two distinct indices. GPU utilization accepts 0.50–0.95, default 0.90. Parking accepts 1–48 GiB, default 32, and 1–8 variable lanes. Suffix-cache launch has no supported mechanism. TASK-26 covers resources, TASK-114 cold starts and TASK-125 the first live pair.

[PR #7](https://github.com/ArborealML/Inference-Router/pull/7) corrects shadowed ownership and selector fields, common Docker key casing and template substitution. Review of 7eda8ad accepted the correction subject to verification. At 6505ea1 all 108 focused tests, the pinned pair config and server Native AOT passed. The CLI separately refused a sibling select/Select pair and a duplicate common env key at the expected paths. Integration head 3af5f10 preserves both SPEC revisions. Continue offline comparison for remaining targets. Before activation, bind receipts and labels to the same canonical model, tier and serving instance.
<!-- SECTION:NOTES:END -->
